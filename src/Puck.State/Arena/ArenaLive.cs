using Puck.Maths;

namespace Puck.State;

/// <summary>The clocks and the declared dynamics rows one live arena read or write evaluates against.</summary>
/// <remarks>The two clocks are independent and neither is derived from the other: <see cref="StateAdvance"/> reads
/// <see cref="EngineTick"/> alone, <see cref="StateCycle"/> and <see cref="StateDynamics"/> read
/// <see cref="Tick"/>. A trait-free read needs neither <see cref="TicksPerSecond"/> nor
/// <see cref="Dynamics"/>.</remarks>
/// <param name="Tick">The simulation tick the read answers as of.</param>
/// <param name="EngineTick">The engine tick the read answers as of, at
/// <see cref="FixedTickConversion.TicksPerSecond"/> per second.</param>
/// <param name="TicksPerSecond">The simulation rate a dynamics follower is stepped at when a write re-seeds it; at
/// zero or below a write stores the new target and leaves the follower's clock where it is. No read consults it: a
/// dynamics cell always reads its stored target here, and <see cref="StateReader.TryReadEased"/> is the eased
/// read.</param>
/// <param name="Dynamics">The declared dynamics rows a <see cref="StateDynamics"/> trait's reference resolves
/// against.</param>
public readonly record struct ArenaTime(ulong Tick, ulong EngineTick, int TicksPerSecond, IReadOnlyList<DynamicsRow>? Dynamics) {
    /// <summary>Gets both clocks at zero — the coordinate a boot load settles a born clock to.</summary>
    public static ArenaTime Origin => At(
        engineTick: 0UL,
        tick: 0UL
    );

    /// <summary>Returns the time pair a row carrying no dynamics trait needs.</summary>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="engineTick">The engine tick.</param>
    /// <returns>The time.</returns>
    public static ArenaTime At(ulong tick, ulong engineTick) => new(
        Dynamics: null,
        EngineTick: engineTick,
        Tick: tick,
        TicksPerSecond: 0
    );
}
public sealed partial class StateArena {
    /// <summary>Resolves which value-over-time trait governs one cell — the cell's own authored trait, its row's
    /// default, or none when the cell's stored <see cref="StateCellBehavior"/> opts out.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The effective behavior, or <see cref="EffectiveBehavior.None"/> when no trait applies.</returns>
    /// <remarks>A row whose layout declares no trait anywhere (<see cref="ArenaRowLayout.HasTraits"/>) answers
    /// none without resolving anything.</remarks>
    public EffectiveBehavior LiveBehavior(int rowOrdinal, CellKey key) {
        if (
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            !layout.HasTraits ||
            !m_catalog.Keys.TryGetName(
            key: key,
            name: out var name
        ) ||
            !TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )
        ) {
            return EffectiveBehavior.None;
        }

        return BehaviorAt(
            name: name,
            row: DocumentRow(rowOrdinal: rowOrdinal),
            slot: slot
        );
    }
    /// <summary>Reads one cell's live value: its stored value advanced or rotated by the trait that governs it, at
    /// <paramref name="time"/>. A cell carrying <see cref="StateDynamics"/> reads its stored target — the truth a
    /// rule, a write's operand, a disclosure and a hash all take; only <see cref="StateReader.TryReadEased"/>
    /// eases, for a presentation binding.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="time">The clocks the traits are evaluated against.</param>
    /// <param name="value">The live value on success; otherwise the carrier holding no case.</param>
    /// <returns><see langword="true"/> when the row holds the cell.</returns>
    /// <remarks>A cell under no trait, and every <see cref="CellKind.Text"/> or <see cref="CellKind.Vector"/>
    /// cell, reads exactly what <see cref="TryRead"/> answers.</remarks>
    public bool TryReadLive(int rowOrdinal, CellKey key, in ArenaTime time, out CellValue value) {
        if (!TryRead(
            key: key,
            rowOrdinal: rowOrdinal,
            value: out value
        )) {
            return false;
        }

        var layout = m_layout[rowOrdinal];

        if (
            !layout.HasTraits ||
            (layout.Kind is (CellKind.Text or CellKind.Vector)) ||
            !m_catalog.Keys.TryGetName(
            key: key,
            name: out var name
        ) ||
            !TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )
        ) {
            return true;
        }

        var row = DocumentRow(rowOrdinal: rowOrdinal);
        var behavior = BehaviorAt(
            name: name,
            row: row,
            slot: slot
        );

        if (behavior.IsNone) {
            return true;
        }

        var live = LiveNumber(
            baseValue: m_numbers[slot],
            behavior: behavior,
            row: row,
            slot: slot,
            time: time
        );

        value = (layout.Kind switch {
            CellKind.Fixed => CellValue.Fixed(rawBits: live),
            CellKind.Bool => CellValue.Bool(value: (live != 0L)),
            _ => CellValue.Int(value: live),
        });

        return true;
    }
    /// <summary>Reads one numeric cell's live value.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="time">The clocks the traits are evaluated against.</param>
    /// <param name="value">The live raw value on success; otherwise zero.</param>
    /// <returns><see langword="true"/> when the row holds a numeric cell there.</returns>
    public bool TryReadLiveNumber(int rowOrdinal, CellKey key, in ArenaTime time, out long value) {
        if (TryReadLive(
            key: key,
            rowOrdinal: rowOrdinal,
            time: time,
            value: out var carried
        )) {
            switch (carried.Kind) {
                case CellKind.Int:
                    value = carried.AsInt;

                    return true;
                case CellKind.Fixed:
                    value = carried.AsFixed;

                    return true;
                case CellKind.Bool:
                    value = (carried.AsBool
                        ? 1L
                        : 0L
                    );

                    return true;
                default:
                    break;
            }
        }

        value = 0L;

        return false;
    }
    /// <summary>Attempts an explicit numeric write against a cell's live value, rebasing the clock the trait reads
    /// from.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="operand">The replacement for a set, or the addend for an add.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="time">The clocks the write settles the cell's clock to.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored.</returns>
    /// <remarks>
    /// An add lands on what a reader sees rather than on the stored base: an accumulating cell holding 10 at engine
    /// tick 0 and advancing 60 per second reads 70 one second later, and an add of three there stores 73 and moves
    /// the cell's epoch to that engine tick. An easing cell keeps chasing from its live sample, with a velocity
    /// kick for its target's jump. A rotating cell adds to its stored phase and keeps its clock where it is — the
    /// phase write is the operation.
    /// A cell under no trait takes the write exactly as <see cref="TryWrite(int, CellKey, long, StateWriteKind, out string)"/>
    /// would.
    /// </remarks>
    public bool TryWriteLive(int rowOrdinal, CellKey key, long operand, StateWriteKind write, in ArenaTime time, out string reason) {
        if (!TryWritableSlot(
            key: key,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return false;
        }

        var layout = m_layout[rowOrdinal];

        if (layout.Kind is (CellKind.Text or CellKind.Vector)) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is a {layout.Kind} row, which takes no numeric write";

            return false;
        }

        var row = DocumentRow(rowOrdinal: rowOrdinal);
        var name = m_catalog.Keys[key];
        var behavior = (layout.HasTraits
            ? BehaviorAt(
                name: name,
                row: row,
                slot: slot
            )
            : EffectiveBehavior.None
        );

        if (behavior.IsNone) {
            return TryWrite(
                key: key,
                operand: operand,
                reason: out reason,
                rowOrdinal: rowOrdinal,
                write: write
            );
        }

        var stored = (Bit(
            index: slot,
            words: m_presence
        )
            ? m_numbers[slot]
            : ((layout.Shape is (RowShape.Lattice or RowShape.Ring))
                ? layout.Empty
                : 0L
            )
        );
        // An add lands on what a reader sees — except on a rotating cell, whose stored value is a phase and whose
        // live value is the rotation the trait carried that phase to. Adding to the rotation would bake this tick's
        // turn into the phase and leave the clock where it is, so the next read would turn it again.
        var current = ((behavior.Cycle is null)
            ? LiveNumber(
                baseValue: stored,
                behavior: behavior,
                row: row,
                slot: slot,
                time: time
            )
            : stored
        );

        _ = m_catalog.TryGetEnum(
            handle: m_catalog.Descriptors[rowOrdinal].Handle,
            symbols: out var symbols
        );

        if (!row.TryAdmitWrite(
            current: current,
            operand: operand,
            reason: out reason,
            stored: out var next,
            symbols: symbols,
            write: write
        )) {
            reason = $"row '{row.Name.Value}' cell '{name.Value}' {reason}";

            return false;
        }
        if (
            (layout.Kind == CellKind.Bool) &&
            (next is not (0L or 1L))
        ) {
            reason = $"row '{row.Name.Value}' cell '{name.Value}' would leave the row's envelope";

            return false;
        }

        // The follower is sampled against the target it is still chasing, so the sample is taken before the write
        // installs the new one.
        var kickedV0 = 0L;
        var kickedY0 = 0L;
        var kicked = ((behavior.Dynamics is { } dynamics) && TryKick(
            dynamics: dynamics,
            newTarget: next,
            row: row,
            slot: slot,
            target: stored,
            time: time,
            v0: out kickedV0,
            y0: out kickedY0
        ));

        StoreNumber(
            next: next,
            previous: stored,
            rowOrdinal: rowOrdinal,
            slot: slot
        );

        if (behavior.Advance is not null) {
            WriteClockAt(
                epochEngineTick: unchecked((long)time.EngineTick),
                epochTick: unchecked((long)time.Tick),
                slot: slot,
                substepTicks: 0L,
                v0: 0L,
                y0: 0L
            );
        } else if (kicked) {
            WriteClockAt(
                epochEngineTick: unchecked((long)time.EngineTick),
                epochTick: unchecked((long)time.Tick),
                slot: slot,
                substepTicks: 0L,
                v0: kickedV0,
                y0: kickedY0
            );
        }

        reason = string.Empty;

        return true;
    }

    private EffectiveBehavior BehaviorAt(StateRow row, CellName name, int slot) {
        var overridden = ((m_behaviors is null)
            ? StateCellBehavior.Inherit
            : ((StateCellBehavior)m_behaviors[slot])
        );
        var isSlotCell = (name == StateRow.SlotKey);

        if (
            !isSlotCell &&
            (overridden == StateCellBehavior.None)
        ) {
            return EffectiveBehavior.None;
        }

        var cell = StateRows.FindCell(
            cells: row.Cells,
            key: name
        );

        // The arena's own column carries the live override, so an authored record that disagrees with it — a cell
        // whose opt-out was written away — resolves against the column.
        if (
            !isSlotCell &&
            (cell is not null) &&
            (cell.Behavior != overridden)
        ) {
            cell = (cell with { Behavior = overridden });
        }

        return EffectiveBehavior.Resolve(
            cell: cell,
            row: row
        );
    }
    private bool HasClock(int slot) => (ReadNumberRaw(
        column: ArenaColumn.ClockSet,
        index: slot
    ) != 0L);
    private bool TryKick(StateRow row, StateDynamics dynamics, int slot, long target, long newTarget, in ArenaTime time, out long y0, out long v0) {
        if (!TrySampleDynamics(
            baseValue: target,
            dynamics: dynamics,
            row: row,
            sample: out var sample,
            slot: slot,
            time: time
        )) {
            v0 = 0L;
            y0 = 0L;

            return false;
        }

        var dynamicsRow = StateRows.FindDynamics(
            dynamics: time.Dynamics,
            name: dynamics.Row
        )!;
        var retargeted = dynamicsRow.Compiled.Retarget(
            current: sample,
            newTarget: StateReader.DynamicsRowRawToFixed(
                raw: newTarget,
                row: row
            ),
            oldTarget: StateReader.DynamicsRowRawToFixed(
                raw: target,
                row: row
            )
        );

        v0 = StateReader.DynamicsFixedToTraitRaw(value: retargeted.Velocity);
        y0 = StateReader.DynamicsFixedToTraitRaw(value: sample.Value);

        return true;
    }
    private long LiveNumber(StateRow row, in EffectiveBehavior behavior, long baseValue, int slot, in ArenaTime time) {
        if (behavior.Advance is { } advance) {
            return advance.ComputeCurrentValue(
                baseValue: baseValue,
                currentEngineTick: time.EngineTick,
                epochEngineTick: ReadNumberRaw(
                    column: ArenaColumn.ClockEpochEngineTick,
                    index: slot
                ),
                row: row
            );
        }
        if (behavior.Cycle is { } cycle) {
            return cycle.ComputeCurrentValue(
                baseValue: baseValue,
                currentTick: time.Tick,
                epochTick: ReadNumberRaw(
                    column: ArenaColumn.ClockEpochTick,
                    index: slot
                ),
                row: row,
                substepTicks: ReadNumberRaw(
                    column: ArenaColumn.ClockSubstepTicks,
                    index: slot
                )
            );
        }

        return baseValue;
    }
    private bool TrySampleDynamics(StateRow row, StateDynamics dynamics, long baseValue, int slot, in ArenaTime time, out SecondOrderSample sample) {
        sample = default;

        if (
            (time.TicksPerSecond <= 0) ||
            (StateRows.FindDynamics(
            dynamics: time.Dynamics,
            name: dynamics.Row
        ) is not { } dynamicsRow)
        ) {
            return false;
        }

        var epochTick = ReadNumberRaw(
            column: ArenaColumn.ClockEpochTick,
            index: slot
        );
        var epoch = ((epochTick < 0L)
            ? 0UL
            : ((ulong)epochTick)
        );
        var target = StateReader.DynamicsRowRawToFixed(
            raw: baseValue,
            row: row
        );

        // A cell whose clock has never settled anywhere starts the follower at its own stored target, at rest,
        // rather than easing in from zero.
        sample = dynamicsRow.Compiled.Evaluate(
            elapsedTicks: ((time.Tick > epoch)
                ? (time.Tick - epoch)
                : 0UL
            ),
            initialValue: (HasClock(slot: slot)
                ? StateReader.DynamicsTraitRawToFixed(raw: ReadNumberRaw(
                    column: ArenaColumn.ClockY0,
                    index: slot
                ))
                : target
            ),
            initialVelocity: StateReader.DynamicsTraitRawToFixed(raw: ReadNumberRaw(
                column: ArenaColumn.ClockV0,
                index: slot
            )),
            target: target,
            ticksPerSecond: ((ulong)time.TicksPerSecond)
        );

        return true;
    }
    private void WriteClockAt(int slot, long epochTick, long epochEngineTick, long y0, long v0, long substepTicks) {
        WriteNumber(
            column: ArenaColumn.ClockEpochTick,
            index: slot,
            value: epochTick
        );
        WriteNumber(
            column: ArenaColumn.ClockEpochEngineTick,
            index: slot,
            value: epochEngineTick
        );
        WriteNumber(
            column: ArenaColumn.ClockY0,
            index: slot,
            value: y0
        );
        WriteNumber(
            column: ArenaColumn.ClockV0,
            index: slot,
            value: v0
        );
        WriteNumber(
            column: ArenaColumn.ClockSubstepTicks,
            index: slot,
            value: substepTicks
        );
        WriteNumber(
            column: ArenaColumn.ClockSet,
            index: slot,
            value: 1L
        );
    }
}
