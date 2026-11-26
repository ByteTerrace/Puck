using Puck.Abstractions.Collections;

namespace Puck.State;

/// <summary>The lane ordinals whose slots in a set of lane rows were touched since their reader last drained them:
/// the cell-level form of <see cref="StateArena.RowGeneration"/>. A touch is any mutation of a watched slot, a write
/// inside an open scope, a write that stored the value already held and a rewind included, so a reader that keeps
/// something derived from each ordinal's slots (a body's kit, look and size) rederives it for the touched ordinals
/// alone, wherever the write stands in its journal scope.</summary>
/// <remarks>One reader owns a watch and registers it through <see cref="StateArena.WatchLaneTouches"/>. A watch no
/// arena holds, and one whose arena changed in a way no ordinal describes (a restored image, a relayout), answers
/// <see cref="Whole"/>: the reader takes every ordinal as touched. Marking and draining cost the ordinals touched,
/// never the lane's capacity.</remarks>
public sealed class ArenaLaneTouches {
    private readonly MarkedIndexSet m_ordinals = new();
    private bool m_whole = true;

    internal StateArena? Arena { get; set; }
    internal int[] Rows { get; set; } = [];

    /// <summary>Gets whether every ordinal must be taken as touched: the watch was just registered, no arena holds
    /// it, or a watched row changed whole.</summary>
    public bool Whole => (m_whole || (Arena is null));
    /// <summary>Gets whether nothing was touched since the last <see cref="Drain"/>.</summary>
    public bool IsEmpty => (!Whole && (m_ordinals.Count == 0));

    /// <summary>Moves the touched ordinals into a caller's array, in the order they were first touched, and starts the
    /// next window, so a touch the reader's own work causes while it visits them is handed over by the next drain.</summary>
    /// <param name="ordinals">The array the ordinals are written to from index zero, grown when it is too short.</param>
    /// <param name="whole">Whether every ordinal must be taken as touched, whatever was written to
    /// <paramref name="ordinals"/>.</param>
    /// <returns>The number of ordinals written.</returns>
    public int Drain(ref int[] ordinals, out bool whole) {
        whole = Whole;
        m_whole = false;

        return m_ordinals.Drain(members: ref ordinals);
    }

    internal void Mark(int ordinal) => _ = m_ordinals.Add(index: ordinal);
    internal void MarkWhole() => m_whole = true;
}
public sealed partial class StateArena {
    // The watches following each lane row's touches, by catalog ordinal; empty until the first watch registers, so an
    // arena no reader follows pays one length test per mutation.
    private List<ArenaLaneTouches>?[] m_laneTouches = [];

    /// <summary>Registers a watch over the touches of some lane rows, replacing whatever it watched before, in this
    /// arena or another. The watch starts <see cref="ArenaLaneTouches.Whole"/>.</summary>
    /// <param name="watch">The reader's watch.</param>
    /// <param name="rows">The lane slot rows' catalog ordinals; an ordinal the layout does not carry, or one that is
    /// no lane slot, is ignored.</param>
    /// <exception cref="ArgumentNullException"><paramref name="watch"/> is <see langword="null"/>.</exception>
    public void WatchLaneTouches(ArenaLaneTouches watch, ReadOnlySpan<int> rows) {
        ArgumentNullException.ThrowIfNull(argument: watch);

        watch.Arena?.ReleaseLaneTouches(watch: watch);
        if (m_laneTouches.Length < m_layout.RowCount) {
            Array.Resize(array: ref m_laneTouches, newSize: m_layout.RowCount);
        }

        var kept = new List<int>(capacity: rows.Length);

        foreach (var row in rows) {
            if ((((uint)row) >= ((uint)m_layout.RowCount)) || (m_layout[row].LaneSlotStart < 0) || kept.Contains(item: row)) {
                continue;
            }

            kept.Add(item: row);
            (m_laneTouches[row] ??= []).Add(item: watch);
        }

        watch.Arena = this;
        watch.Rows = [.. kept];
        watch.MarkWhole();
    }

    private void ReleaseLaneTouches(ArenaLaneTouches watch) {
        foreach (var row in watch.Rows) {
            if (((uint)row) < ((uint)m_laneTouches.Length)) {
                _ = m_laneTouches[row]?.Remove(item: watch);
            }
        }

        watch.Arena = null;
        watch.Rows = [];
    }
    // The storage the watches were registered against is replaced whole (Adopt): the row ordinals they name may name
    // other rows now, so every watch is let go and answers Whole until its reader registers it again.
    private void ReleaseLaneTouches() {
        foreach (var watches in m_laneTouches) {
            while (watches is { Count: > 0 }) {
                ReleaseLaneTouches(watch: watches[^1]);
            }
        }

        m_laneTouches = [];
    }
    // One column position of a row was mutated: the lane ordinal it belongs to is touched, or the row whole when the
    // position is no single ordinal's.
    private void NoteLaneTouch(int row, ArenaColumn column, int index) {
        if ((((uint)row) >= ((uint)m_laneTouches.Length)) || (m_laneTouches[row] is not { Count: > 0 } watches)) {
            return;
        }

        var ordinal = ((column == ArenaColumn.LaneRoster)
            ? (index - m_layout.LaneRosterBase(lane: m_layout[row].Lane))
            : ((ArenaColumns.Space(column: column) == ArenaIndexSpace.LaneSlot) ? (index - m_layout[row].LaneSlotStart) : -1));

        foreach (var watch in watches) {
            if (ordinal >= 0) { watch.Mark(ordinal: ordinal); } else { watch.MarkWhole(); }
        }
    }
    // The one door a row's generation moves through when no single position describes the mutation (a restored
    // image, a moved reservation): every watch over the row takes it whole.
    private void BumpRowGeneration(int row) {
        m_rowGenerations[row]++;
        if ((((uint)row) < ((uint)m_laneTouches.Length)) && (m_laneTouches[row] is { } watches)) {
            foreach (var watch in watches) { watch.MarkWhole(); }
        }
    }
}
