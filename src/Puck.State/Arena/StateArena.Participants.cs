namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Returns a value indicating whether one ordinal of a slot lane has joined.</summary>
    /// <param name="lane">The participant or identity lane.</param>
    /// <param name="ordinal">The lane ordinal.</param>
    /// <returns><see langword="true"/> when the ordinal is joined.</returns>
    public bool IsJoined(StateLane lane, int ordinal) => (
        (lane is (StateLane.Participant or StateLane.Identity)) &&
        (((uint)ordinal) < ((uint)m_layout.Options.Capacity(lane: lane))) &&
        Bit(
        index: (m_layout.LaneRosterBase(lane: lane) + ordinal),
        words: m_laneRoster
    )
    );
    /// <summary>Attempts to admit one named ordinal of a slot lane, zeroing every slot it will answer.</summary>
    /// <param name="lane">The participant or identity lane.</param>
    /// <param name="ordinal">The ordinal to admit.</param>
    /// <param name="reason">Why the join was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the ordinal joined.</returns>
    /// <remarks>The caller names the ordinal, because the host that owns the roster — a seat table, a body table —
    /// already decided which slot the participant occupies; an arena-chosen ordinal would be a second allocation
    /// beside it.</remarks>
    public bool TryJoin(StateLane lane, int ordinal, out string reason) {
        if (lane is not (StateLane.Participant or StateLane.Identity)) {
            reason = $"lane '{lane}' admits no ordinals";

            return false;
        }

        var capacity = m_layout.Options.Capacity(lane: lane);

        if (((uint)ordinal) >= ((uint)capacity)) {
            reason = $"lane '{lane}' admits {capacity} ordinals, so ordinal {ordinal} lies outside it";

            return false;
        }

        var rosterBase = m_layout.LaneRosterBase(lane: lane);

        if (Bit(
            index: (rosterBase + ordinal),
            words: m_laneRoster
        )) {
            reason = $"lane '{lane}' ordinal {ordinal} has already joined";

            return false;
        }

        WriteNumber(
            column: ArenaColumn.LaneRoster,
            index: (rosterBase + ordinal),
            value: 1L
        );
        ClearLaneSlots(
            lane: lane,
            ordinal: ordinal
        );

        reason = string.Empty;

        return true;
    }
    /// <summary>Attempts to release one ordinal of a slot lane, clearing every slot it answered.</summary>
    /// <param name="lane">The participant or identity lane.</param>
    /// <param name="ordinal">The ordinal to release.</param>
    /// <param name="reason">Why the leave was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the ordinal left.</returns>
    public bool TryLeave(StateLane lane, int ordinal, out string reason) {
        if (!IsJoined(
            lane: lane,
            ordinal: ordinal
        )) {
            reason = $"lane '{lane}' ordinal {ordinal} has not joined";

            return false;
        }

        ClearLaneSlots(
            lane: lane,
            ordinal: ordinal
        );
        WriteNumber(
            column: ArenaColumn.LaneRoster,
            index: (m_layout.LaneRosterBase(lane: lane) + ordinal),
            value: 0L
        );

        reason = string.Empty;

        return true;
    }
    /// <summary>Attempts to read one lane slot for one ordinal.</summary>
    /// <param name="rowOrdinal">The lane slot's catalog ordinal.</param>
    /// <param name="ordinal">The lane ordinal.</param>
    /// <param name="value">The slot's value on success; otherwise the carrier holding no case.</param>
    /// <returns><see langword="true"/> when the ordinal has joined and the descriptor is a slot of its lane.</returns>
    public bool TryReadSlot(int rowOrdinal, int ordinal, out CellValue value) {
        if (TryLaneSlot(
            index: out var index,
            layout: out var layout,
            ordinal: ordinal,
            rowOrdinal: rowOrdinal
        )) {
            value = ((layout.Kind == CellKind.Fixed)
                ? CellValue.Fixed(rawBits: m_laneNumbers[index])
                : CellValue.Int(value: m_laneNumbers[index])
            );

            return true;
        }

        value = default;

        return false;
    }
    /// <summary>Attempts to write one lane slot for one ordinal.</summary>
    /// <param name="rowOrdinal">The lane slot's catalog ordinal.</param>
    /// <param name="ordinal">The lane ordinal.</param>
    /// <param name="operand">The replacement for a set, or the addend for an add.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was stored.</returns>
    public bool TryWriteSlot(int rowOrdinal, int ordinal, long operand, StateWriteKind write, out string reason) {
        if (!TryLaneSlot(
            index: out var index,
            layout: out _,
            ordinal: ordinal,
            rowOrdinal: rowOrdinal
        )) {
            reason = $"lane slot '{RowName(rowOrdinal: rowOrdinal)}' answers no ordinal {ordinal}";

            return false;
        }

        var current = m_laneNumbers[index];
        var exact = ((write == StateWriteKind.Add)
            ? (((Int128)current) + operand)
            : ((Int128)operand)
        );

        if (
            (exact < long.MinValue) ||
            (exact > long.MaxValue)
        ) {
            reason = $"lane slot '{RowName(rowOrdinal: rowOrdinal)}' ordinal {ordinal} would overflow 64-bit storage";

            return false;
        }

        WriteNumber(
            column: ArenaColumn.LaneNumber,
            index: index,
            value: ((long)exact)
        );

        reason = string.Empty;

        return true;
    }

    private void ClearLaneSlots(StateLane lane, int ordinal) {
        var descriptor = m_catalog.Lane(lane: lane);

        for (var rowOrdinal = descriptor.FirstOrdinal; (rowOrdinal < (descriptor.FirstOrdinal + descriptor.Count)); rowOrdinal++) {
            if (rowOrdinal < 0) {
                break;
            }

            ref readonly var layout = ref m_layout[rowOrdinal];

            if (layout.LaneSlotStart >= 0) {
                WriteNumber(
                    column: ArenaColumn.LaneNumber,
                    index: (layout.LaneSlotStart + ordinal),
                    value: 0L
                );
            }
        }
    }
    private bool TryLaneSlot(int rowOrdinal, int ordinal, out ArenaRowLayout layout, out int index) {
        index = -1;

        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            layout = default;

            return false;
        }

        layout = m_layout[rowOrdinal];

        if (
            (layout.LaneSlotStart < 0) ||
            !IsJoined(
            lane: layout.Lane,
            ordinal: ordinal
        )
        ) {
            return false;
        }

        index = (layout.LaneSlotStart + ordinal);

        return true;
    }
}
