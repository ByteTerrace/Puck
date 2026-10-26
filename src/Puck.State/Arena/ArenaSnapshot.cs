namespace Puck.State;

/// <summary>One retained numeric column position.</summary>
public readonly record struct ArenaNumberImage(ArenaColumn Column, int Index, long Value);
/// <summary>One retained text position.</summary>
public readonly record struct ArenaTextImage(int Index, string Value);
/// <summary>One retained audience position.</summary>
public readonly record struct ArenaVisibilityImage(int Index, StateVisibility Value);
/// <summary>One retained observation position.</summary>
public readonly record struct ArenaObservationImage(int Index, StateObservation Value);
/// <summary>The arena's own continuation, independent of document row serialization.</summary>
public sealed record ArenaSnapshot(int[] Capacities, CellName[] Keys, ArenaNumberImage[] Numbers, ArenaTextImage[] Text,
    ArenaVisibilityImage[] Visibility, ArenaObservationImage[] Observations, sbyte[] Vectors);

public sealed partial class StateArena {
    /// <summary>Captures columns, admitted reservations and key identities at a settled boundary.</summary>
    public ArenaSnapshot CaptureSnapshot() {
        if (Journal.Scopes != 0) { throw new InvalidOperationException(message: "Cannot checkpoint an open state transaction."); }
        var capacities = new int[m_layout.RowCount];
        for (var row = 0; row < capacities.Length; row++) { capacities[row] = m_layout[row].CellCapacity; }
        var numbers = new List<ArenaNumberImage>();
        var text = new List<ArenaTextImage>();
        var visibility = new List<ArenaVisibilityImage>();
        var observations = new List<ArenaObservationImage>();
        foreach (var column in ArenaColumns.All) {
            if (column is ArenaColumn.CellCapacity or ArenaColumn.Vector) { continue; }
            for (var index = 0; index < m_layout.Size(column: column); index++) {
                var row = m_layout.RowOf(column: column, index: index);
                if ((row >= 0) && (!m_layout.HasColumn(row: row, column: column) || m_layout[row].HostOwned)) { continue; }
                switch (column) {
                    case ArenaColumn.Text:
                        if (ReadReferenceRaw(column: column, index: index) is string value) { text.Add(new(Index: index, Value: value)); }
                        break;
                    case ArenaColumn.Visibility:
                        if (ReadReferenceRaw(column: column, index: index) is StateVisibility policy) { visibility.Add(new(Index: index, Value: policy)); }
                        break;
                    case ArenaColumn.Observation:
                        if (ReadReferenceRaw(column: column, index: index) is StateObservation observation) { observations.Add(new(Index: index, Value: observation)); }
                        break;
                    default:
                        var raw = ReadNumberRaw(column: column, index: index);
                        if (raw != (column == ArenaColumn.MemberKey ? -1L : 0L)) { numbers.Add(new(Column: column, Index: index, Value: raw)); }
                        break;
                }
            }
        }
        return new ArenaSnapshot(Capacities: capacities, Keys: m_keys.Names.ToArray(), Numbers: numbers.ToArray(),
            Text: text.ToArray(), Visibility: visibility.ToArray(), Observations: observations.ToArray(), Vectors: (sbyte[])m_vectors.Clone());
    }

    /// <summary>Restores into a separately prepared arena. The caller adopts it only after validation succeeds.</summary>
    public void RestoreSnapshot(ArenaSnapshot snapshot) {
        if ((Journal.Scopes != 0) || (snapshot.Capacities.Length != m_layout.RowCount)) { throw new InvalidOperationException(message: "The checkpoint's arena shape differs from its declarations."); }
        var layout = m_layout;
        for (var row = 0; row < snapshot.Capacities.Length; row++) {
            var capacity = snapshot.Capacities[row];
            if (capacity == layout[row].CellCapacity) { continue; }
            if ((capacity < 0) || (capacity > m_rows[row].CellCeiling) || !layout.HasColumn(row: row, column: ArenaColumn.CellCapacity)) {
                throw new InvalidOperationException(message: "The checkpoint carries an inadmissible reservation.");
            }
            layout = layout.WithCellCapacity(rowOrdinal: row, capacity: capacity);
        }
        if (!AdmitReservation(next: layout, additionalBytes: 0L, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
        InstallReservation(next: layout);
        if (!TryRestoreKeys(names: snapshot.Keys, reason: out reason) || snapshot.Keys.Length != m_keys.Count) { throw new InvalidOperationException(message: reason); }
        foreach (var column in ArenaColumns.All) {
            if (column is ArenaColumn.CellCapacity or ArenaColumn.Vector) { continue; }
            for (var index = 0; index < m_layout.Size(column: column); index++) {
                var row = m_layout.RowOf(column: column, index: index);
                if ((row >= 0) && (!m_layout.HasColumn(row: row, column: column) || m_layout[row].HostOwned)) { continue; }
                if (column is ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation) {
                    if (ReadReferenceRaw(column: column, index: index) is not null) { WriteReferenceRaw(column: column, index: index, value: null); }
                } else if (!ArenaLayout.IsOptional(column: column) || ((row >= 0) && IsMaterialized(column: column, row: row))) {
                    WriteNumberRaw(column: column, index: index, value: column == ArenaColumn.MemberKey ? -1L : 0L);
                }
            }
        }
        foreach (var entry in snapshot.Numbers) {
            RequireSnapshotPosition(column: entry.Column, index: entry.Index);
            if (entry.Column is ArenaColumn.CellCapacity or ArenaColumn.Vector or ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation) { throw new InvalidOperationException(message: "The checkpoint carries a nonnumeric column as a number."); }
            if (entry.Column == ArenaColumn.MemberKey && (entry.Value < -1L || entry.Value >= m_keys.Count)) { throw new InvalidOperationException(message: "The checkpoint carries an invalid key ordinal."); }
            var value = entry.Value;
            if (entry.Column == ArenaColumn.MemberKey && value >= 0L) {
                if (!m_keys.TryResolve(snapshot.Keys[(int)value], out var key)) { throw new InvalidOperationException("The checkpoint key did not bind."); }
                value = key.Ordinal;
            }
            WriteNumberRaw(column: entry.Column, index: entry.Index, value: value);
        }
        foreach (var entry in snapshot.Text) {
            RequireSnapshotPosition(column: ArenaColumn.Text, index: entry.Index);
            if (entry.Value.Length > StateCapacity.MaxTextValueLength) { throw new InvalidOperationException(message: "The checkpoint carries oversized text."); }
            WriteReferenceRaw(column: ArenaColumn.Text, index: entry.Index, value: entry.Value);
        }
        foreach (var entry in snapshot.Visibility) {
            RequireSnapshotPosition(column: ArenaColumn.Visibility, index: entry.Index);
            if (!StateVisibilityStorage.TryNormalize(value: entry.Value, normalized: out var policy, bytes: out _, reason: out reason)) { throw new InvalidOperationException(message: reason); }
            WriteReferenceRaw(column: ArenaColumn.Visibility, index: entry.Index, value: policy);
        }
        foreach (var entry in snapshot.Observations) {
            RequireSnapshotPosition(column: ArenaColumn.Observation, index: entry.Index);
            WriteReferenceRaw(column: ArenaColumn.Observation, index: entry.Index, value: entry.Value);
        }
        if (snapshot.Vectors.Length != m_vectors.Length || Bytes > ArenaCapacity.MaxBytes) { throw new InvalidOperationException(message: "The checkpoint exceeds the admitted arena."); }
        snapshot.Vectors.CopyTo(m_vectors, 0);
        for (var row = 0; row < m_layout.RowCount; row++) {
            if (m_layout[row].IsStored) { Reindex(rowOrdinal: row, layout: m_layout[row]); }
            m_rowVersions[row]++; m_rowGenerations[row]++;
        }
        // Admission at this cold boundary checks values and metadata using the ordinary importer.
        m_poolMutationDepth++;
        try {
            if (!TryValidateLoad(rows: ExportRows(includePoolRows: true), reason: out reason) || !TryValidateRelations(out reason)) { throw new InvalidOperationException(message: reason); }
        } finally { m_poolMutationDepth--; }
        Array.Fill(m_deliveryRows, true);
    }

    private void RequireSnapshotPosition(ArenaColumn column, int index) {
        if (!Enum.IsDefined(column) || (index < 0) || (index >= m_layout.Size(column: column))) { throw new InvalidOperationException(message: "The checkpoint carries an invalid column position."); }
        var row = m_layout.RowOf(column: column, index: index);
        if ((row >= 0) && (!m_layout.HasColumn(row: row, column: column) || m_layout[row].HostOwned)) { throw new InvalidOperationException(message: "The checkpoint writes an undeclared column."); }
    }
}
