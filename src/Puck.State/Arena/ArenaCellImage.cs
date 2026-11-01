namespace Puck.State;

/// <summary>A stored cell crossing a snapshot boundary. It carries no authorization or declaration.</summary>
public readonly record struct ArenaCellImage(CellName Key, CellValue Value, StateCellBehavior Behavior = StateCellBehavior.Inherit, StateCellClock? Clock = null);

public sealed partial class StateArena {
    /// <summary>Reads the stored columns needed by a presentation mirror.</summary>
    public ArenaCellImage ReadCellImage(int rowOrdinal, CellKey key) {
        TryRead(rowOrdinal: rowOrdinal, key: key, value: out var value);
        StateCellClock? clock = null;
        if (TryReadClock(rowOrdinal: rowOrdinal, key: key, epochTick: out var tick, epochEngineTick: out var engine,
            y0: out var y0, v0: out var v0, substepTicks: out var substep, set: out var set) && set) {
            clock = new StateCellClock(EpochTick: tick, EpochEngineTick: engine, Y0: y0, V0: v0, SubstepTicks: substep);
        }
        return new ArenaCellImage(Key: m_keys[key], Value: value, Behavior: Behavior(rowOrdinal: rowOrdinal, key: key), Clock: clock);
    }

    /// <summary>Starts replacing a mirror row from a committed snapshot, outside gameplay transaction execution.</summary>
    public void BeginProjectionRow(int rowOrdinal, int count) {
        var reason = "a journal scope is open";
        if ((Journal.Scopes != 0) || !TryReserveCells(rowOrdinal: rowOrdinal, required: count, reason: out reason)) {
            throw new InvalidOperationException(message: "Cannot import a projection row: " + reason);
        }
        ref readonly var layout = ref m_layout[rowOrdinal];
        if (!layout.IsStored) { return; }
        ClearRowStorage(rowOrdinal: rowOrdinal, layout: in layout);
        if (layout.Shape is RowShape.Keyed or RowShape.Ordered) {
            WriteNumber(column: ArenaColumn.MemberCount, index: rowOrdinal, value: count);
            InitializeMemberLinks(rowOrdinal: rowOrdinal, count: count);
        }
    }

    /// <summary>Returns whether a mirror row can take a replacement of <paramref name="count"/> cells position by
    /// position through <see cref="TryRefreshProjectionCell"/>: a keyed or ordered row already holding that many
    /// members, whose cursors and masks are as a replacement leaves them, outside gameplay transaction execution.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="count">The replacement's cell count.</param>
    /// <returns><see langword="false"/> when the replacement must begin with <see cref="BeginProjectionRow"/>.</returns>
    public bool HoldsProjectionShape(int rowOrdinal, int count) {
        if ((Journal.Scopes != 0) || (((uint)rowOrdinal) >= ((uint)m_layout.RowCount))) { return false; }
        ref readonly var layout = ref m_layout[rowOrdinal];

        if (!layout.IsStored || (layout.Shape is not (RowShape.Keyed or RowShape.Ordered)) || m_catalog.IsPoolRow(rowOrdinal: rowOrdinal) ||
            (layout.Dimensions > 0) || (m_memberCounts[rowOrdinal] != count) || (m_historyCursors[rowOrdinal] != 0L) ||
            (ReadNumberRaw(column: ArenaColumn.DrawCursor, index: rowOrdinal) != 0L) ||
            (ReadNumberRaw(column: ArenaColumn.PhaseSequence, index: rowOrdinal) != 0L)) {
            return false;
        }
        for (var word = 0; ((layout.MaskWordStart >= 0) && (word < (layout.MaskCount * 4))); word++) {
            if (ReadNumberRaw(column: ArenaColumn.DrawnMaskWord, index: (layout.MaskWordStart + word)) != 0L) { return false; }
        }
        return true;
    }

    /// <summary>Brings one position of a mirror row to a committed snapshot's cell, writing only what differs from
    /// what the row's replacement would store there. The row must hold the replacement's shape
    /// (<see cref="HoldsProjectionShape"/>) and the image's key at that position already.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="position">The cell's position within the row.</param>
    /// <param name="image">The disclosed cell.</param>
    /// <param name="moved">Whether the position's stored cell changed.</param>
    /// <returns><see langword="false"/> when the position holds another key, so the row must be replaced whole.</returns>
    public bool TryRefreshProjectionCell(int rowOrdinal, int position, in ArenaCellImage image, out bool moved) {
        moved = false;
        ref readonly var layout = ref m_layout[rowOrdinal];

        if (((uint)position) >= ((uint)m_memberCounts[rowOrdinal]) || (image.Value.Kind != layout.Kind) ||
            !m_keys.TryResolve(key: out var key, name: image.Key)) {
            return false;
        }
        var slot = CellSlotAt(layout: layout, position: position);

        if (m_memberKeys[slot] != key.Ordinal) { return false; }
        if (StoresProjection(layout: in layout, rowOrdinal: rowOrdinal, slot: slot, image: in image)) { return true; }
        ClearCell(rowOrdinal: rowOrdinal, slot: slot);
        if (!TryAdmitValue(rowOrdinal: rowOrdinal, layout: in layout, row: m_rows[rowOrdinal], name: image.Key,
            value: image.Value, admitted: out var admitted, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
        StoreValueRaw(layout: in layout, slot: slot, value: admitted);
        WriteNumber(column: ArenaColumn.MemberKey, index: slot, value: key.Ordinal);
        if (layout.HasColumn(column: ArenaColumn.Behavior)) { WriteNumber(column: ArenaColumn.Behavior, index: slot, value: (long)image.Behavior); }
        if (image.Clock is { } clock) {
            WriteClockAt(slot: slot, epochTick: clock.EpochTick, epochEngineTick: clock.EpochEngineTick,
                y0: clock.Y0, v0: clock.V0, substepTicks: clock.SubstepTicks);
        }
        moved = true;
        return true;
    }

    // Whether a slot already holds exactly what clearing it and importing the image would leave there.
    private bool StoresProjection(in ArenaRowLayout layout, int rowOrdinal, int slot, in ArenaCellImage image) {
        var stored = (Bit(index: slot, words: m_presence) ? ValueAt(layout: in layout, slot: slot) : default);
        var behavior = (layout.HasColumn(column: ArenaColumn.Behavior) ? (long)image.Behavior : 0L);
        var clock = image.Clock;

        return ((image.Value.HasValue ? stored.Equals(other: image.Value) : !stored.HasValue) &&
            (ReadNumberRaw(column: ArenaColumn.Behavior, index: slot) == behavior) &&
            (HasClock(slot: slot) == (clock is not null)) &&
            (ReadNumberRaw(column: ArenaColumn.ClockEpochTick, index: slot) == (clock?.EpochTick ?? 0L)) &&
            (ReadNumberRaw(column: ArenaColumn.ClockEpochEngineTick, index: slot) == (clock?.EpochEngineTick ?? 0L)) &&
            (ReadNumberRaw(column: ArenaColumn.ClockY0, index: slot) == (clock?.Y0 ?? 0L)) &&
            (ReadNumberRaw(column: ArenaColumn.ClockV0, index: slot) == (clock?.V0 ?? 0L)) &&
            (ReadNumberRaw(column: ArenaColumn.ClockSubstepTicks, index: slot) == (clock?.SubstepTicks ?? 0L)) &&
            (!IsMaterialized(column: ArenaColumn.Text, row: rowOrdinal) || (layout.Kind == CellKind.Text) || (m_texts![slot] is null)) &&
            (!IsMaterialized(column: ArenaColumn.Visibility, row: rowOrdinal) || (m_visibilities![slot] is null)) &&
            (!IsMaterialized(column: ArenaColumn.Observation, row: rowOrdinal) || (m_observations![slot] is null)));
    }

    /// <summary>Imports a disclosed cell into a presentation arena using its ordinary storage kernels.</summary>
    public void ImportProjectionCell(int rowOrdinal, int position, in ArenaCellImage image) {
        ref readonly var layout = ref m_layout[rowOrdinal];
        if (!layout.IsStored) { return; }
        var fixedPosition = (layout.Shape is RowShape.Lattice or RowShape.Ring) || m_catalog.IsPoolRow(rowOrdinal: rowOrdinal);
        var index = fixedPosition ? int.Parse(image.Key.Value, System.Globalization.CultureInfo.InvariantCulture) : position;
        if (((uint)index >= (uint)layout.CellCapacity) || (image.Value.Kind != layout.Kind)) { throw new InvalidOperationException(message: "Projection cell is outside its declaration."); }
        var slot = layout.CellStart + index;
        var key = m_keys.Intern(name: image.Key);
        if (!TryAdmitValue(rowOrdinal: rowOrdinal, layout: in layout, row: m_rows[rowOrdinal], name: image.Key,
            value: image.Value, admitted: out var admitted, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
        StoreValueRaw(layout: in layout, slot: slot, value: admitted);
        if (layout.Shape is RowShape.Slot or RowShape.Keyed or RowShape.Ordered) {
            WriteNumber(column: ArenaColumn.MemberKey, index: slot, value: key.Ordinal);
            m_slotOfKey[rowOrdinal][key.Ordinal] = slot;
        }
        if (layout.HasColumn(column: ArenaColumn.Behavior)) { WriteNumber(column: ArenaColumn.Behavior, index: slot, value: (long)image.Behavior); }
        if (image.Clock is { } clock) {
            WriteClockAt(slot: slot, epochTick: clock.EpochTick, epochEngineTick: clock.EpochEngineTick,
                y0: clock.Y0, v0: clock.V0, substepTicks: clock.SubstepTicks);
        }
    }
}
