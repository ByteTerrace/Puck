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
