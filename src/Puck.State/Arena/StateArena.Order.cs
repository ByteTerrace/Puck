namespace Puck.State;

public sealed partial class StateArena {
    private static bool UsesLinks(in ArenaRowLayout layout) => layout.HasColumn(column: ArenaColumn.NextSlot);
    private static bool IsLinkColumn(ArenaColumn column) => ((ArenaLayout.LinkColumns & (1UL << (int)column)) != 0UL);
    private int HashCellExtent(in ArenaRowLayout layout) => UsesLinks(layout: layout) ? (int)m_memberCounts[layout.Ordinal] : layout.CellCapacity;

    // Positional views are derived from the links once per row generation. Head/tail operations never build one.
    private int CellSlotAt(in ArenaRowLayout layout, int position) {
        if (!UsesLinks(layout: layout)) { return layout.CellStart + position; }
        var row = layout.Ordinal;
        var count = (int)m_memberCounts[row];
        if (position == 0) { return layout.CellStart + m_headSlots[row] - 1; }
        if (position == count - 1) { return layout.CellStart + m_tailSlots[row] - 1; }
        var positions = m_positions[row];
        if ((positions is null) || (positions.Length < layout.CellCapacity)) {
            positions = new int[layout.CellCapacity];
            m_positions[row] = positions;
            m_positionVersions[row] = ulong.MaxValue;
        }
        if (m_positionVersions[row] != m_rowGenerations[row]) {
            var slot = m_headSlots[row];
            for (var index = 0; index < count; index++) {
                positions[index] = layout.CellStart + slot - 1;
                slot = m_nextSlots[positions[index]];
            }
            m_positionVersions[row] = m_rowGenerations[row];
            Visit(lanes: count);
        }
        return positions[position];
    }

    private void InitializeMemberLinks(int rowOrdinal, int count) {
        ref readonly var layout = ref m_layout[rowOrdinal];
        if (!UsesLinks(layout: layout)) { return; }
        for (var position = 0; position < layout.CellCapacity; position++) {
            var slot = layout.CellStart + position;
            WriteNumber(column: ArenaColumn.PreviousSlot, index: slot, value: ((position < count) && (position > 0)) ? position : 0);
            WriteNumber(column: ArenaColumn.NextSlot, index: slot, value: position + 1 < count ? position + 2 : 0);
        }
        WriteNumber(column: ArenaColumn.HeadSlot, index: rowOrdinal, value: count == 0 ? 0 : 1);
        WriteNumber(column: ArenaColumn.TailSlot, index: rowOrdinal, value: count);
    }

    private int InsertMemberSlot(int rowOrdinal, int position, int reusableSlot) {
        ref readonly var layout = ref m_layout[rowOrdinal];
        var slot = reusableSlot >= 0 ? reusableSlot : FindFreeMemberSlot(layout: layout);
        var local = slot - layout.CellStart + 1;
        var before = position == m_memberCounts[rowOrdinal] ? 0 : CellSlotAt(layout: layout, position: position) - layout.CellStart + 1;
        var previous = before == 0 ? m_tailSlots[rowOrdinal] : m_previousSlots[layout.CellStart + before - 1];
        WriteNumber(column: ArenaColumn.PreviousSlot, index: slot, value: previous);
        WriteNumber(column: ArenaColumn.NextSlot, index: slot, value: before);
        if (previous == 0) { WriteNumber(column: ArenaColumn.HeadSlot, index: rowOrdinal, value: local); }
        else { WriteNumber(column: ArenaColumn.NextSlot, index: layout.CellStart + previous - 1, value: local); }
        if (before == 0) { WriteNumber(column: ArenaColumn.TailSlot, index: rowOrdinal, value: local); }
        else { WriteNumber(column: ArenaColumn.PreviousSlot, index: layout.CellStart + before - 1, value: local); }
        return slot;
    }

    private int FindFreeMemberSlot(in ArenaRowLayout layout) {
        var end = layout.CellStart + layout.CellCapacity;
        for (var start = layout.CellStart; start < end;) {
            var offset = start & 63;
            var bits = Math.Min(val1: 64 - offset, val2: end - start);
            var mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1UL;
            var free = (~m_presence[start >> 6] >> offset) & mask;
            Visit(lanes: 1L);
            if (free != 0UL) { return start + System.Numerics.BitOperations.TrailingZeroCount(value: free); }
            start += bits;
        }
        throw new InvalidOperationException(message: "The admitted member has no free cell slot.");
    }
}
