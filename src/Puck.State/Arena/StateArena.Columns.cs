namespace Puck.State;

public sealed partial class StateArena {
    private static bool Bit(ReadOnlySpan<ulong> words, int index) => (((words[(index >> 6)] >> (index & 63)) & 1UL) != 0UL);
    private static void SetBit(Span<ulong> words, int index, bool value) {
        var mask = (1UL << (index & 63));

        if (value) {
            words[(index >> 6)] |= mask;
        } else {
            words[(index >> 6)] &= ~mask;
        }
    }
    private static long[] Ensure(ref long[]? column, int size) => (column ??= new long[size]);
    // The lane whose roster run holds the bit: the layout placed the runs, so it answers.
    private StateLaneDescriptor LaneOfRoster(int rosterIndex) {
        var lane = StateLane.Participant;

        foreach (var candidate in Enum.GetValues<StateLane>()) {
            var start = m_layout.LaneRosterBase(lane: candidate);

            if (
                (m_layout.Options.Capacity(lane: candidate) > 0) &&
                (rosterIndex >= start) &&
                (rosterIndex < (start + m_layout.Options.Capacity(lane: candidate)))
            ) {
                lane = candidate;

                break;
            }
        }

        return m_catalog.Lane(lane: lane);
    }
    // Whether a lazily allocated column has been materialized; every other column always exists. A column that is
    // not materialized reads as zero or null everywhere, so a clear has nothing to do to it.
    private bool IsMaterialized(ArenaColumn column) => (column switch {
        ArenaColumn.ClockEpochTick => (m_clockEpochTicks is not null),
        ArenaColumn.ClockEpochEngineTick => (m_clockEpochEngineTicks is not null),
        ArenaColumn.ClockY0 => (m_clockY0 is not null),
        ArenaColumn.ClockV0 => (m_clockV0 is not null),
        ArenaColumn.ClockSubstepTicks => (m_clockSubstepTicks is not null),
        ArenaColumn.Behavior => (m_behaviors is not null),
        ArenaColumn.DrawCursor => (m_drawCursors is not null),
        ArenaColumn.DrawnMaskWord => (m_maskWords is not null),
        ArenaColumn.PhaseSequence => (m_phaseSequences is not null),
        ArenaColumn.Text => (m_texts is not null),
        ArenaColumn.Provenance => (m_provenance is not null),
        ArenaColumn.Visibility => (m_visibilities is not null),
        ArenaColumn.Observation => (m_observations is not null),
        _ => true,
    });
    private long ReadNumberRaw(ArenaColumn column, int index) => (column switch {
        ArenaColumn.Number => m_numbers[index],
        ArenaColumn.Presence => (Bit(
        index: index,
        words: m_presence
    )
            ? 1L
            : 0L
        ),
        ArenaColumn.MemberKey => m_memberKeys[index],
        ArenaColumn.ClockSet => (Bit(
        index: index,
        words: m_clockSet
    )
            ? 1L
            : 0L
        ),
        ArenaColumn.ClockEpochTick => ((m_clockEpochTicks is null) ? 0L : m_clockEpochTicks[index]),
        ArenaColumn.ClockEpochEngineTick => ((m_clockEpochEngineTicks is null) ? 0L : m_clockEpochEngineTicks[index]),
        ArenaColumn.ClockY0 => ((m_clockY0 is null) ? 0L : m_clockY0[index]),
        ArenaColumn.ClockV0 => ((m_clockV0 is null) ? 0L : m_clockV0[index]),
        ArenaColumn.ClockSubstepTicks => ((m_clockSubstepTicks is null) ? 0L : m_clockSubstepTicks[index]),
        ArenaColumn.Behavior => ((m_behaviors is null) ? 0L : m_behaviors[index]),
        ArenaColumn.MemberCount => m_memberCounts[index],
        ArenaColumn.HistoryCursor => m_historyCursors[index],
        ArenaColumn.DrawCursor => ((m_drawCursors is null) ? 0L : m_drawCursors[index]),
        ArenaColumn.DrawnMaskWord => ((m_maskWords is null) ? 0L : m_maskWords[index]),
        ArenaColumn.PhaseSequence => ((m_phaseSequences is null) ? 0L : m_phaseSequences[index]),
        ArenaColumn.LaneNumber => m_laneNumbers[index],
        ArenaColumn.LaneRoster => (Bit(
        index: index,
        words: m_laneRoster
    )
            ? 1L
            : 0L
        ),
        _ => throw new InvalidOperationException(message: $"Arena column '{column}' stores no number."),
    });
    private object? ReadReferenceRaw(ArenaColumn column, int index) => (column switch {
        ArenaColumn.Text => m_texts?[index],
        ArenaColumn.Provenance => m_provenance?[index],
        ArenaColumn.Visibility => m_visibilities?[index],
        ArenaColumn.Observation => m_observations?[index],
        _ => throw new InvalidOperationException(message: $"Arena column '{column}' stores no reference."),
    });
    private void WriteNumberRaw(ArenaColumn column, int index, long value) {
        switch (column) {
            case ArenaColumn.Number:
                m_numbers[index] = value;

                break;
            case ArenaColumn.Presence:
                SetBit(
                    index: index,
                    value: (value != 0L),
                    words: m_presence
                );

                break;
            case ArenaColumn.ClockSet:
                SetBit(
                    index: index,
                    value: (value != 0L),
                    words: m_clockSet
                );

                break;
            case ArenaColumn.MemberKey:
                m_memberKeys[index] = ((int)value);

                break;
            case ArenaColumn.ClockEpochTick:
                Ensure(
                    column: ref m_clockEpochTicks,
                    size: m_layout.CellSlotCount
                )[index] = value;

                break;
            case ArenaColumn.ClockEpochEngineTick:
                Ensure(
                    column: ref m_clockEpochEngineTicks,
                    size: m_layout.CellSlotCount
                )[index] = value;

                break;
            case ArenaColumn.ClockY0:
                Ensure(
                    column: ref m_clockY0,
                    size: m_layout.CellSlotCount
                )[index] = value;

                break;
            case ArenaColumn.ClockV0:
                Ensure(
                    column: ref m_clockV0,
                    size: m_layout.CellSlotCount
                )[index] = value;

                break;
            case ArenaColumn.ClockSubstepTicks:
                Ensure(
                    column: ref m_clockSubstepTicks,
                    size: m_layout.CellSlotCount
                )[index] = value;

                break;
            case ArenaColumn.Behavior:
                (m_behaviors ??= new byte[m_layout.CellSlotCount])[index] = ((byte)value);

                break;
            case ArenaColumn.MemberCount:
                m_memberCounts[index] = value;

                break;
            case ArenaColumn.HistoryCursor:
                m_historyCursors[index] = value;

                break;
            case ArenaColumn.DrawCursor:
                Ensure(
                    column: ref m_drawCursors,
                    size: m_layout.RowCount
                )[index] = value;

                break;
            case ArenaColumn.DrawnMaskWord:
                Ensure(
                    column: ref m_maskWords,
                    size: m_layout.MaskWordCount
                )[index] = value;

                break;
            case ArenaColumn.PhaseSequence:
                Ensure(
                    column: ref m_phaseSequences,
                    size: m_layout.RowCount
                )[index] = value;

                break;
            case ArenaColumn.LaneNumber:
                m_laneNumbers[index] = value;

                break;
            case ArenaColumn.LaneRoster:
                SetBit(
                    index: index,
                    value: (value != 0L),
                    words: m_laneRoster
                );

                break;
            default:
                throw new InvalidOperationException(message: $"Arena column '{column}' stores no number.");
        }
    }
    private void WriteReferenceRaw(ArenaColumn column, int index, object? value) {
        switch (column) {
            case ArenaColumn.Text:
                (m_texts ??= new string?[m_layout.CellSlotCount])[index] = ((string?)value);

                break;
            case ArenaColumn.Provenance:
                (m_provenance ??= new string?[m_layout.CellSlotCount])[index] = ((string?)value);

                break;
            case ArenaColumn.Visibility:
                (m_visibilities ??= new StateVisibility?[m_layout.CellSlotCount])[index] = ((StateVisibility?)value);

                break;
            case ArenaColumn.Observation:
                (m_observations ??= new StateObservation?[m_layout.CellSlotCount])[index] = ((StateObservation?)value);

                break;
            default:
                throw new InvalidOperationException(message: $"Arena column '{column}' stores no reference.");
        }
    }
    // The one door every number column is written through: it journals what it overwrote while a scope is open,
    // settles the version immediately when none is, and moves the row's generation either way.
    private void WriteNumber(ArenaColumn column, int index, long value, bool tailPush = false) {
        var previous = ReadNumberRaw(
            column: column,
            index: index
        );

        if (m_journal.Scopes > 0) {
            m_journal.Record(
                column: column,
                index: index,
                previous: previous
            );
        } else if (previous != value) {
            m_changeEpoch++;

            MarkVersion(
                column: column,
                index: index
            );
        }

        WriteNumberRaw(
            column: column,
            index: index,
            value: value
        );
        BumpGeneration(
            column: column,
            index: index,
            tailPush: tailPush
        );
    }
    private void WriteReference(ArenaColumn column, int index, object? value, bool tailPush = false) {
        var previous = ReadReferenceRaw(
            column: column,
            index: index
        );

        if (m_journal.Scopes > 0) {
            m_journal.RecordReference(
                column: column,
                index: index,
                previous: previous
            );
        } else if (!Equals(
            objA: previous,
            objB: value
        )) {
            m_changeEpoch++;

            MarkVersion(
                column: column,
                index: index
            );
        }

        WriteReferenceRaw(
            column: column,
            index: index,
            value: value
        );
        BumpGeneration(
            column: column,
            index: index,
            tailPush: tailPush
        );
    }
    private bool Differs(ArenaJournalEntry entry) {
        if (entry.Column == ArenaColumn.Vector) {
            return !m_journal.Components(entry: entry).SequenceEqual(other: VectorSpan(slot: entry.Index));
        }

        if (ArenaColumns.IsReference(column: entry.Column)) {
            return !Equals(
                objA: entry.Reference,
                objB: ReadReferenceRaw(
                    column: entry.Column,
                    index: entry.Index
                )
            );
        }

        return (entry.Number != ReadNumberRaw(
            column: entry.Column,
            index: entry.Index
        ));
    }
    private void Restore(ArenaJournalEntry entry) {
        if (entry.Column == ArenaColumn.Vector) {
            m_journal.Components(entry: entry).CopyTo(destination: VectorSpan(slot: entry.Index));

            return;
        }

        if (ArenaColumns.IsReference(column: entry.Column)) {
            WriteReferenceRaw(
                column: entry.Column,
                index: entry.Index,
                value: entry.Reference
            );

            return;
        }

        WriteNumberRaw(
            column: entry.Column,
            index: entry.Index,
            value: entry.Number
        );
    }
}
