using Puck.Maths;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Finds a held key's logical position in its row.</summary>
    /// <param name="rowOrdinal">The row ordinal.</param>
    /// <param name="key">The held key.</param>
    /// <param name="position">Its position, or -1 when absent.</param>
    public bool TryPositionOf(int rowOrdinal, CellKey key, out int position) {
        position = -1;
        if (!TryCellSlot(key: key, rowOrdinal: rowOrdinal, slot: out var slot)) { return false; }
        ref readonly var layout = ref m_layout[rowOrdinal];

        if (!UsesLinks(layout: layout)) { position = (slot - layout.CellStart); return true; }
        // The ends answer from the links; any other slot reads the inverse the row's position view carries.
        if (slot == ((layout.CellStart + m_headSlots[rowOrdinal]) - 1)) { position = 0; return true; }
        if (slot == ((layout.CellStart + m_tailSlots[rowOrdinal]) - 1)) { position = (((int)m_memberCounts[rowOrdinal]) - 1); return true; }
        position = PositionView(layout: layout)[(layout.CellCapacity + (slot - layout.CellStart))];
        return true;
    }

    private static bool UsesLinks(in ArenaRowLayout layout) => layout.HasColumn(column: ArenaColumn.NextSlot);
    private static bool IsLinkColumn(ArenaColumn column) => ((ArenaLayout.LinkColumns & (1UL << ((int)column))) != 0UL);
    private int HashCellExtent(in ArenaRowLayout layout) => (UsesLinks(layout: layout) ? (int)m_memberCounts[layout.Ordinal] : layout.CellCapacity);
    // Positional views are derived from the links once per row generation. Head/tail operations never build one.
    private int CellSlotAt(in ArenaRowLayout layout, int position) {
        if (!UsesLinks(layout: layout)) { return (layout.CellStart + position); }
        var row = layout.Ordinal;
        var count = ((int)m_memberCounts[row]);

        if (position == 0) { return ((layout.CellStart + m_headSlots[row]) - 1); }
        if (position == (count - 1)) { return ((layout.CellStart + m_tailSlots[row]) - 1); }
        return PositionView(layout: layout)[position];
    }
    // The row's position view, derived from its links by one walk per row generation: the first CellCapacity entries
    // answer a position's slot, and the CellCapacity after them a slot's position (indexed by its offset in the row),
    // so a key's position and a position's key are both one read. The walk that fills one fills the other.
    private int[] PositionView(in ArenaRowLayout layout) {
        var row = layout.Ordinal;
        var capacity = layout.CellCapacity;
        var view = m_positions[row];

        if ((view is null) || (view.Length != (2 * capacity))) {
            view = new int[(2 * capacity)];
            m_positions[row] = view;
            m_positionVersions[row] = ulong.MaxValue;
        }
        if (m_positionVersions[row] != m_rowGenerations[row]) {
            var count = ((int)m_memberCounts[row]);
            var slot = m_headSlots[row];

            for (var index = 0; (index < count); index++) {
                var at = ((layout.CellStart + slot) - 1);

                view[index] = at;
                view[(capacity + (at - layout.CellStart))] = index;
                slot = m_nextSlots[at];
            }
            m_positionVersions[row] = m_rowGenerations[row];
            Visit(lanes: count);
        }
        return view;
    }
    private void InitializeMemberLinks(int rowOrdinal, int count, IReadOnlyList<int>? slots = null) {
        ref readonly var layout = ref m_layout[rowOrdinal];

        if (!UsesLinks(layout: layout)) { return; }
        for (var position = 0; (position < layout.CellCapacity); position++) {
            WriteNumber(column: ArenaColumn.PreviousSlot, index: (layout.CellStart + position), value: 0);
            WriteNumber(column: ArenaColumn.NextSlot, index: (layout.CellStart + position), value: 0);
        }
        int Local(int position) => ((slots?[position] ?? position) + 1);
        for (var position = 0; (position < count); position++) {
            var slot = ((layout.CellStart + Local(position: position)) - 1);

            WriteNumber(column: ArenaColumn.PreviousSlot, index: slot, value: ((position > 0) ? Local(position: (position - 1)) : 0));
            WriteNumber(column: ArenaColumn.NextSlot, index: slot, value: (((position + 1) < count) ? Local(position: (position + 1)) : 0));
        }
        WriteNumber(column: ArenaColumn.HeadSlot, index: rowOrdinal, value: ((count == 0) ? 0 : Local(position: 0)));
        WriteNumber(column: ArenaColumn.TailSlot, index: rowOrdinal, value: ((count == 0) ? 0 : Local(position: (count - 1))));
    }
    private int InsertMemberSlot(int rowOrdinal, int position, int reusableSlot) {
        ref readonly var layout = ref m_layout[rowOrdinal];
        var slot = ((reusableSlot >= 0) ? reusableSlot : FindFreeMemberSlot(layout: layout));
        var local = ((slot - layout.CellStart) + 1);
        var before = ((position == m_memberCounts[rowOrdinal]) ? 0 : ((CellSlotAt(layout: layout, position: position) - layout.CellStart) + 1));
        var previous = ((before == 0) ? m_tailSlots[rowOrdinal] : m_previousSlots[((layout.CellStart + before) - 1)]);

        WriteNumber(column: ArenaColumn.PreviousSlot, index: slot, value: previous);
        WriteNumber(column: ArenaColumn.NextSlot, index: slot, value: before);
        if (previous == 0) { WriteNumber(column: ArenaColumn.HeadSlot, index: rowOrdinal, value: local); } else { WriteNumber(column: ArenaColumn.NextSlot, index: ((layout.CellStart + previous) - 1), value: local); }
        if (before == 0) { WriteNumber(column: ArenaColumn.TailSlot, index: rowOrdinal, value: local); } else { WriteNumber(column: ArenaColumn.PreviousSlot, index: ((layout.CellStart + before) - 1), value: local); }
        return slot;
    }
    private int FindFreeMemberSlot(in ArenaRowLayout layout) {
        var end = (layout.CellStart + layout.CellCapacity);

        for (var start = layout.CellStart; (start < end);) {
            var offset = start & 63;
            var bits = Math.Min(val1: (64 - offset), val2: (end - start));
            var mask = bits.LowMask<ulong>();
            var free = (~m_presence[(start >> 6)] >> offset) & mask;

            Visit(lanes: 1L);
            if (free != 0UL) { return (start + System.Numerics.BitOperations.TrailingZeroCount(value: free)); }
            start += bits;
        }
        throw new InvalidOperationException(message: "The admitted member has no free cell slot.");
    }
}
