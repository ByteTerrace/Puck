namespace Puck.State;

public sealed partial class StateArena {
    private int m_restoreJournalLimit = -1;
    /// <summary>Reserves room for a compound member operation before it changes membership.</summary>
    /// <param name="rowOrdinal">The row ordinal.</param>
    /// <param name="required">The required member count.</param>
    /// <param name="reason">The refusal reason.</param>
    public bool TryReserveCells(int rowOrdinal, int required, out string reason) {
        reason = string.Empty;
        if ((required < 0) || ((uint)rowOrdinal >= (uint)m_layout.RowCount)) { reason = "Invalid row reservation."; return false; }
        if (required <= m_layout[rowOrdinal].CellCapacity) { return true; }
        if (!m_layout[rowOrdinal].HasColumn(column: ArenaColumn.CellCapacity) || (required > DocumentRow(rowOrdinal: rowOrdinal).CellCeiling)) {
            reason = "destination zone is full";
            return false;
        }
        return TryGrowRow(rowOrdinal: rowOrdinal, required: required, name: default, reason: out reason);
    }
    /// <summary>Gets or sets the host's deterministic admission of a proposed storage reservation. A null result
    /// admits it; a reason refuses it before keys, cells or the journal change.</summary>
    public Func<ArenaLayout, string?>? GrowthAdmission { get; set; }
    /// <summary>Gets deterministic work performed while changing storage reservations, including rewinds.</summary>
    public long ReservationWorkCharged { get; private set; }

    /// <summary>Returns the deterministic work charged for copying and reindexing a changed reservation.</summary>
    /// <param name="layout">The proposed layout.</param>
    public static long ReservationWork(ArenaLayout layout) => (8L * layout.CellSlotCount) + (2L * layout.VectorByteCount) + ((long)layout.RowCount * (ArenaColumns.All.Length + 8L));

    private int GrowthCapacity(int rowOrdinal, int required) {
        var ceiling = DocumentRow(rowOrdinal: rowOrdinal).CellCeiling;
        if (required > ceiling) { throw new InvalidOperationException(message: $"row growth exceeds its {ceiling}-cell ceiling"); }
        var capacity = Math.Max(val1: 1, val2: m_layout[rowOrdinal].CellCapacity);
        while (capacity < required) { capacity = Math.Min(val1: ceiling, val2: capacity * 2); }
        return capacity;
    }

    private bool TryGrowRow(int rowOrdinal, int required, CellName name, out string reason) {
        try {
            var next = m_layout.WithCellCapacity(rowOrdinal: rowOrdinal, capacity: GrowthCapacity(rowOrdinal: rowOrdinal, required: required));
            var keyBytes = name == default || m_keys.ContainsLocal(name: name) ? 0L : CellKeyTable.EntryBytes(name: name);
            if (!AdmitReservation(next: next, additionalBytes: keyBytes, reason: out reason)) { return false; }
            ApplyReservation(next: next);
            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message;
            return false;
        }
    }

    private bool AdmitReservation(ArenaLayout next, long additionalBytes, out string reason) {
        var vectorBytes = 0L;
        for (var ordinal = 0; ordinal < next.RowCount; ordinal++) {
            if (m_catalog.IsPoolRow(rowOrdinal: ordinal)) { continue; }
            ref readonly var row = ref next[ordinal];
            if (!row.IsStored || row.Kind != CellKind.Vector) { continue; }
            var bytes = (long)row.CellCapacity * row.Dimensions;
            if (bytes > StateCapacity.MaxVectorRowBytes) {
                reason = $"State row '{ordinal}' reserves {bytes} vector bytes, past the {StateCapacity.MaxVectorRowBytes}-byte row ceiling.";
                return false;
            }
            vectorBytes += bytes;
        }
        if (vectorBytes > StateCapacity.MaxVectorSectionBytes) {
            reason = $"Vector storage reaches {vectorBytes} bytes, past the {StateCapacity.MaxVectorSectionBytes}-byte section ceiling.";
            return false;
        }
        if (next.Bytes + m_declarationVisibilityBytes + m_visibilityBytes + m_keys.Bytes + additionalBytes > ArenaCapacity.MaxBytes) {
            reason = $"arena growth exceeds the {ArenaCapacity.MaxBytes}-byte ceiling";
            return false;
        }
        if (m_undo is not null) {
            var bytes = 0L;
            foreach (var group in m_undo.Values) {
                bytes += EstimateUndoGroupBytes(catalog: m_catalog, layout: next, rows: group.Rows, depth: group.Depth);
            }
            if (bytes > ArenaCapacity.MaxJournalBytes) {
                reason = $"arena growth exceeds the {ArenaCapacity.MaxJournalBytes}-byte retained journal ceiling";
                return false;
            }
        }
        reason = GrowthAdmission?.Invoke(next) ?? string.Empty;
        return reason.Length == 0;
    }

    private void ApplyReservation(ArenaLayout next) {
        for (var row = 0; row < m_layout.RowCount; row++) {
            var previous = m_layout[row].CellCapacity;
            if (previous == next[row].CellCapacity) { continue; }
            if (m_journal.Scopes > 0) { m_journal.Record(column: ArenaColumn.CellCapacity, index: row, previous: previous); }
            else {
                RetainDirectWrite(column: ArenaColumn.CellCapacity, index: row, number: previous);
                m_changeEpoch++;
                MarkRowVersion(rowOrdinal: row);
            }
            m_rowGenerations[row]++;
        }
        InstallReservation(next: next);
    }

    private void InstallReservation(ArenaLayout next) {
        var previous = m_layout;
        var numbers = new long[next.CellSlotCount];
        var keys = new int[next.CellSlotCount];
        Array.Fill(array: keys, value: -1);
        var presence = new ulong[(next.CellSlotCount + 63) / 64];
        var vectors = new sbyte[next.VectorByteCount];
        foreach (var row in previous.ColumnRows) {
            ref readonly var source = ref previous[row];
            ref readonly var target = ref next[row];
            if (!source.IsStored) { continue; }
            var count = Math.Min(val1: source.CellCapacity, val2: target.CellCapacity);
            m_numbers.AsSpan(start: source.CellStart, length: count).CopyTo(destination: numbers.AsSpan(start: target.CellStart));
            m_memberKeys.AsSpan(start: source.CellStart, length: count).CopyTo(destination: keys.AsSpan(start: target.CellStart));
            for (var position = 0; position < count; position++) {
                SetBit(words: presence, index: target.CellStart + position, value: Bit(words: m_presence, index: source.CellStart + position));
            }
            if (source.Dimensions > 0) {
                m_vectors.AsSpan(start: source.VectorByteStart, length: count * source.Dimensions).CopyTo(destination: vectors.AsSpan(start: target.VectorByteStart));
            }
            if (source.CellStart != target.CellStart) { m_rowGenerations[row]++; }
        }
        m_journal.RemapLayout(previous: previous, next: next, count: m_restoreJournalLimit >= 0 ? m_restoreJournalLimit : m_journal.Length);
        for (var index = m_windowEntries.Count - 1; index >= 0; index--) {
            var entry = m_windowEntries[index];
            var row = previous.RowOf(column: entry.Column, index: entry.Index);
            if ((ArenaColumns.Space(column: entry.Column) == ArenaIndexSpace.Cell) && (entry.Index - previous[row].CellStart >= next[row].CellCapacity)) {
                WindowOpaque(column: ArenaColumn.MemberCount, index: row);
                m_windowEntries.RemoveAt(index: index);
            } else { m_windowEntries[index] = previous.Remap(entry: entry, next: next); }
        }
        if (m_undo is not null) {
            foreach (var group in m_undo.Values) {
                group.RemapLayout(previous: previous, next: next,
                    entryCapacity: UndoEntryCapacity(catalog: m_catalog, layout: next, rows: group.Rows), componentCapacity: UndoComponentCapacity(layout: next, rows: group.Rows));
            }
        }
        m_layout = next;
        m_numbers = numbers;
        m_memberKeys = keys;
        m_presence = presence;
        m_vectors = vectors;
        m_clockSet.ChangeLayout(next: next);
        m_nextSlots.ChangeLayout(next: next);
        m_previousSlots.ChangeLayout(next: next);
        m_headSlots.ChangeLayout(next: next);
        m_tailSlots.ChangeLayout(next: next);
        m_clockEpochTicks?.ChangeLayout(next: next);
        m_clockEpochEngineTicks?.ChangeLayout(next: next);
        m_clockY0?.ChangeLayout(next: next);
        m_clockV0?.ChangeLayout(next: next);
        m_clockSubstepTicks?.ChangeLayout(next: next);
        m_behaviors?.ChangeLayout(next: next);
        m_texts?.ChangeLayout(next: next);
        m_visibilities?.ChangeLayout(next: next);
        m_observations?.ChangeLayout(next: next);
        m_drawCursors?.ChangeLayout(next: next);
        m_phaseSequences?.ChangeLayout(next: next);
        Array.Clear(array: m_changeStamp);
        Array.Clear(array: m_changeDiffers);
        Array.Clear(array: m_windowStamp);
        foreach (var entry in m_windowEntries) {
            var stamps = m_windowStamp[(int)entry.Column] ??= new ArenaColumnBuffer<long>(layout: next, column: entry.Column);
            stamps[entry.Index] = m_windowEpoch;
        }
        Array.Clear(array: m_positions);
        for (var row = 0; row < next.RowCount; row++) { Reindex(rowOrdinal: row, layout: next[row]); }
        if (m_catalog.Pools.Count != 0) { m_poolAddresses = BuildPoolAddresses(catalog: m_catalog, layout: next, occupancy: m_poolOccupancy); }
        var work = ReservationWork(layout: next);
        ReservationWorkCharged += work;
        Visit(lanes: work);
    }
}
