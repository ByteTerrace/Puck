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
    private ArenaColumnBuffer<long> Ensure(ref ArenaColumnBuffer<long>? column, ArenaColumn kind) => (column ??= new ArenaColumnBuffer<long>(column: kind, layout: m_layout));
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
    private bool IsMaterialized(ArenaColumn column, int row) => (m_layout.HasColumn(column: column, row: row) && (column switch {
        ArenaColumn.ClockEpochTick => (m_clockEpochTicks?.HasRow(row: row) == true),
        ArenaColumn.ClockEpochEngineTick => (m_clockEpochEngineTicks?.HasRow(row: row) == true),
        ArenaColumn.ClockY0 => (m_clockY0?.HasRow(row: row) == true),
        ArenaColumn.ClockV0 => (m_clockV0?.HasRow(row: row) == true),
        ArenaColumn.ClockSubstepTicks => (m_clockSubstepTicks?.HasRow(row: row) == true),
        ArenaColumn.Behavior => (m_behaviors?.HasRow(row: row) == true),
        ArenaColumn.DrawCursor => (m_drawCursors?.HasRow(row: row) == true),
        ArenaColumn.DrawnMaskWord => (m_maskWords is not null),
        ArenaColumn.PhaseSequence => (m_phaseSequences?.HasRow(row: row) == true),
        ArenaColumn.Text => (m_texts?.HasRow(row: row) == true),
        ArenaColumn.Visibility => (m_visibilities?.HasRow(row: row) == true),
        ArenaColumn.Observation => (m_observations?.HasRow(row: row) == true),
        ArenaColumn.ClockSet => m_clockSet.HasRow(row: row),
        _ => true,
    }));
    private long ReadNumberRaw(ArenaColumn column, int index) => (column switch {
        ArenaColumn.Number => m_numbers[index],
        ArenaColumn.CellCapacity => m_layout[index].CellCapacity,
        ArenaColumn.NextSlot => m_nextSlots[index],
        ArenaColumn.PreviousSlot => m_previousSlots[index],
        ArenaColumn.HeadSlot => m_headSlots[index],
        ArenaColumn.TailSlot => m_tailSlots[index],
        ArenaColumn.Presence => (Bit(
        index: index,
        words: m_presence
    )
            ? 1L
            : 0L
        ),
        ArenaColumn.MemberKey => m_memberKeys[index],
        ArenaColumn.ClockSet => (m_clockSet[index]
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
        ArenaColumn.Visibility => m_visibilities?[index],
        ArenaColumn.Observation => m_observations?[index],
        _ => throw new InvalidOperationException(message: $"Arena column '{column}' stores no reference."),
    });
    private void WriteNumberRaw(ArenaColumn column, int index, long value) {
        switch (column) {
            case ArenaColumn.NextSlot: m_nextSlots[index] = ((int)value); break;
            case ArenaColumn.PreviousSlot: m_previousSlots[index] = ((int)value); break;
            case ArenaColumn.HeadSlot: m_headSlots[index] = ((int)value); break;
            case ArenaColumn.TailSlot: m_tailSlots[index] = ((int)value); break;
            case ArenaColumn.CellCapacity: InstallReservation(next: m_layout.WithCellCapacity(capacity: ((int)value), rowOrdinal: index)); break;
            case ArenaColumn.Number:
                m_numbers[index] = value;

                break;
            case ArenaColumn.Presence:
                SetBit(
                    index: index,
                    value: (value != 0L),
                    words: m_presence
                );
                if (m_poolOfDomainRow.Length != 0) {
                    var row = m_layout.RowOf(column: column, index: index);
                    var pool = m_poolOfDomainRow[row];

                    if (pool >= 0) {
                        SetBit(words: m_poolOccupancy[pool], index: (index - m_layout[row].CellStart), value: (value != 0L));
                    }
                }

                break;
            case ArenaColumn.ClockSet:
                m_clockSet[index] = (value != 0L);

                break;
            case ArenaColumn.MemberKey:
                m_memberKeys[index] = ((int)value);

                break;
            case ArenaColumn.ClockEpochTick:
                Ensure(
                    column: ref m_clockEpochTicks,
                    kind: ArenaColumn.ClockEpochTick
                )[index] = value;

                break;
            case ArenaColumn.ClockEpochEngineTick:
                Ensure(
                    column: ref m_clockEpochEngineTicks,
                    kind: ArenaColumn.ClockEpochEngineTick
                )[index] = value;

                break;
            case ArenaColumn.ClockY0:
                Ensure(
                    column: ref m_clockY0,
                    kind: ArenaColumn.ClockY0
                )[index] = value;

                break;
            case ArenaColumn.ClockV0:
                Ensure(
                    column: ref m_clockV0,
                    kind: ArenaColumn.ClockV0
                )[index] = value;

                break;
            case ArenaColumn.ClockSubstepTicks:
                Ensure(
                    column: ref m_clockSubstepTicks,
                    kind: ArenaColumn.ClockSubstepTicks
                )[index] = value;

                break;
            case ArenaColumn.Behavior:
                (m_behaviors ??= new ArenaColumnBuffer<byte>(column: ArenaColumn.Behavior, layout: m_layout))[index] = ((byte)value);

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
                    kind: ArenaColumn.DrawCursor
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
                    kind: ArenaColumn.PhaseSequence
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
                (m_texts ??= new ArenaColumnBuffer<string?>(column: ArenaColumn.Text, layout: m_layout))[index] = ((string?)value);

                break;
            case ArenaColumn.Visibility:
                m_visibilityBytes -= StateVisibilityStorage.RetainedBytes(value: m_visibilities?[index]);
                (m_visibilities ??= new ArenaColumnBuffer<StateVisibility?>(column: ArenaColumn.Visibility, layout: m_layout))[index] = ((StateVisibility?)value);
                m_visibilityBytes += StateVisibilityStorage.RetainedBytes(value: ((StateVisibility?)value));

                break;
            case ArenaColumn.Observation:
                (m_observations ??= new ArenaColumnBuffer<StateObservation?>(column: ArenaColumn.Observation, layout: m_layout))[index] = ((StateObservation?)value);

                break;
            default:
                throw new InvalidOperationException(message: $"Arena column '{column}' stores no reference.");
        }
    }
    // The one door for a committed change outside every journal scope, which no rewind can take back: a direct write's
    // position, or the rows a restore rewrote (BeginOutOfScope, then the row overload once per restored position).
    // Either marks the row for delivery and moves its version once per change, makes the row opaque to an open change
    // window and invalidates every undo group holding it; a direct write also hands what it overwrote to the
    // executing undo pass. Publication reads only the delivery marks (CopyChangedRows), so a writer that skips this
    // door changes state no session receives. The generation moves at every raw write, scoped or not, so each writer
    // moves it itself.
    private void MarkOutOfScope(in ArenaJournalEntry previous, ReadOnlySpan<sbyte> components = default) {
        m_changeEpoch++;
        WindowOpaque(column: previous.Column, index: previous.Index);
        RetainWrite(components: components, differs: null, entry: previous);
        MarkVersion(column: previous.Column, index: previous.Index);
    }
    private OutOfScopeChange BeginOutOfScope() => new(Epoch: ++m_changeEpoch);
    // One restored row of the change BeginOutOfScope opened. The restore's own undo group is the one it rewinds, so it
    // is left as the restore found it.
    private void MarkOutOfScope(OutOfScopeChange change, int row, ArenaUndoGroup? restored) {
        if (change.Epoch != m_changeEpoch) {
            throw new InvalidOperationException(message: "Another arena change began inside a restore.");
        }
        if ((row < 0) || (m_rowChangeStamp[row] == change.Epoch)) {
            return;
        }
        WindowOpaque(row: row);
        InvalidateUndo(except: restored, row: row);
        MarkRowVersion(rowOrdinal: row);
    }
    // Only BeginOutOfScope makes one, so a restore cannot mark rows without opening its change first.
    private readonly record struct OutOfScopeChange(long Epoch);

    // The one door every number column is written through: it journals what it overwrote while a scope is open,
    // settles the version immediately when none is, and moves the row's generation either way.
    private void WriteNumber(ArenaColumn column, int index, long value) {
        var row = m_layout.RowOf(column: column, index: index);

        if ((row >= 0) && !m_layout.HasColumn(column: column, row: row)) {
            if (value == 0L) { return; }
            throw new InvalidOperationException(message: $"Row '{RowName(rowOrdinal: row)}' declares no '{column}' column.");
        }
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
            MarkOutOfScope(previous: new ArenaJournalEntry(Column: column, Index: index, Number: previous, Reference: null));
        }

        WriteNumberRaw(
            column: column,
            index: index,
            value: value
        );
        BumpGeneration(
            column: column,
            index: index
        );
    }
    private void WriteReference(ArenaColumn column, int index, object? value) {
        var row = m_layout.RowOf(column: column, index: index);

        if ((row >= 0) && !m_layout.HasColumn(column: column, row: row)) {
            if (value is null) { return; }
            throw new InvalidOperationException(message: $"Row '{RowName(rowOrdinal: row)}' declares no '{column}' column.");
        }
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
            MarkOutOfScope(previous: new ArenaJournalEntry(Column: column, Index: index, Number: 0L, Reference: previous));
        }

        WriteReferenceRaw(
            column: column,
            index: index,
            value: value
        );
        BumpGeneration(
            column: column,
            index: index
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
    private void Restore(ArenaJournalEntry entry, ReadOnlySpan<sbyte> components) {
        if (entry.Column == ArenaColumn.Vector) {
            components.CopyTo(destination: VectorSpan(slot: entry.Index));

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
