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
        var image = CaptureImage(positions: ImagePositions(layout: m_layout, rows: null), omitDefaults: true);
        return new ArenaSnapshot(Capacities: capacities, Keys: m_keys.Names.ToArray(), Numbers: image.Numbers,
            Text: image.Text, Visibility: image.Visibility, Observations: image.Observations, Vectors: (sbyte[])m_vectors.Clone());
    }

    /// <summary>Restores into a separately prepared arena. The caller adopts it only after validation succeeds.</summary>
    public void RestoreSnapshot(ArenaSnapshot snapshot) {
        if ((Journal.Scopes != 0) || (snapshot.Capacities.Length != m_layout.RowCount)) { throw new InvalidOperationException(message: "The checkpoint's arena shape differs from its declarations."); }
        InstallReservation(next: SnapshotLayout(capacities: snapshot.Capacities));
        if (!TryRestoreKeys(names: snapshot.Keys, reason: out var reason) || snapshot.Keys.Length != m_keys.Count) { throw new InvalidOperationException(message: reason); }
        foreach (var (column, index) in ImagePositions(layout: m_layout, rows: null)) {
            var row = m_layout.RowOf(column: column, index: index);
            if (column is ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation) {
                if (ReadReferenceRaw(column: column, index: index) is not null) { WriteReferenceRaw(column: column, index: index, value: null); }
            } else if (!ArenaLayout.IsOptional(column: column) || ((row >= 0) && IsMaterialized(column: column, row: row))) {
                WriteNumberRaw(column: column, index: index, value: column == ArenaColumn.MemberKey ? -1L : 0L);
            }
        }
        foreach (var entry in snapshot.Numbers) {
            RequireSnapshotPosition(column: entry.Column, index: entry.Index);
            if (entry.Column is ArenaColumn.CellCapacity or ArenaColumn.Vector or ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation) { throw new InvalidOperationException(message: "The checkpoint carries a nonnumeric column as a number."); }
            RequireImagePosition(layout: m_layout, column: entry.Column, index: entry.Index, number: entry.Value, reference: null, what: "checkpoint");
            var value = entry.Value;
            if (entry.Column == ArenaColumn.MemberKey && value >= 0L) {
                if (!m_keys.TryResolve(snapshot.Keys[(int)value], out var key)) { throw new InvalidOperationException("The checkpoint key did not bind."); }
                value = key.Ordinal;
            }
            WriteNumberRaw(column: entry.Column, index: entry.Index, value: value);
        }
        foreach (var entry in snapshot.Text) {
            RequireSnapshotPosition(column: ArenaColumn.Text, index: entry.Index);
            RequireImagePosition(layout: m_layout, column: ArenaColumn.Text, index: entry.Index, number: 0L, reference: entry.Value, what: "checkpoint");
            WriteReferenceRaw(column: ArenaColumn.Text, index: entry.Index, value: entry.Value);
        }
        foreach (var entry in snapshot.Visibility) {
            RequireSnapshotPosition(column: ArenaColumn.Visibility, index: entry.Index);
            RequireImagePosition(layout: m_layout, column: ArenaColumn.Visibility, index: entry.Index, number: 0L, reference: entry.Value, what: "checkpoint");
            WriteReferenceRaw(column: ArenaColumn.Visibility, index: entry.Index, value: entry.Value);
        }
        foreach (var entry in snapshot.Observations) {
            RequireSnapshotPosition(column: ArenaColumn.Observation, index: entry.Index);
            RequireImagePosition(layout: m_layout, column: ArenaColumn.Observation, index: entry.Index, number: 0L, reference: entry.Value, what: "checkpoint");
            WriteReferenceRaw(column: ArenaColumn.Observation, index: entry.Index, value: entry.Value);
        }
        if (snapshot.Vectors.Length != m_vectors.Length || Bytes > ArenaCapacity.MaxBytes) { throw new InvalidOperationException(message: "The checkpoint exceeds the admitted arena."); }
        snapshot.Vectors.CopyTo(m_vectors, 0);
        for (var row = 0; row < m_layout.RowCount; row++) {
            if (m_layout[row].IsStored) { Reindex(rowOrdinal: row, layout: m_layout[row]); }
            BumpRowVersion(rowOrdinal: row); m_rowGenerations[row]++;
        }
        // Admission at this cold boundary checks values and metadata using the ordinary importer.
        m_poolMutationDepth++;
        try {
            if (!TryValidateLoad(rows: ExportRows(includePoolRows: true), reason: out reason) || !TryValidateRelations(out reason)) { throw new InvalidOperationException(message: reason); }
        } finally { m_poolMutationDepth--; }
        Array.Fill(m_deliveryRows, true);
    }

    // Full and scoped snapshots restore reservations through the same admitted relayout mechanism. A scope
    // replaces only its reservations; other rows keep their current capacity and contents.
    private ArenaLayout SnapshotLayout(IReadOnlyList<int> capacities, IReadOnlyList<int>? rows = null) {
        var layout = m_layout;
        for (var index = 0; index < capacities.Count; index++) {
            var row = rows is null ? index : rows[index];
            if ((uint)row >= (uint)layout.RowCount) { throw new InvalidOperationException("The snapshot names an undeclared row."); }
            var capacity = capacities[index];
            if (capacity == layout[row].CellCapacity) { continue; }
            if ((capacity < 0) || (capacity > m_rows[row].CellCeiling) || !layout.HasColumn(row: row, column: ArenaColumn.CellCapacity)) {
                throw new InvalidOperationException(message: "The snapshot carries an inadmissible reservation.");
            }
            layout = layout.WithCellCapacity(rowOrdinal: row, capacity: capacity);
        }
        // A snapshot whose every reservation equals the live one grows nothing, so it is not a proposal: admission
        // (whose growth hook prices the document's rules against the proposed layout) runs only for a changed one.
        if (ReferenceEquals(objA: layout, objB: m_layout)) { return layout; }
        if (!AdmitReservation(next: layout, additionalBytes: 0L, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
        return layout;
    }

    // The column positions a snapshot carries: with no row set, every position a declared, arena-owned column of a
    // row (or no row) holds; with one, the positions of those rows' declared columns. The full and the scoped
    // snapshot both walk this, for capture and for restore.
    private static IEnumerable<(ArenaColumn Column, int Index)> ImagePositions(ArenaLayout layout, IReadOnlySet<int>? rows) {
        for (var ordinal = 0; (ordinal < ArenaColumns.All.Length); ordinal++) {
            var column = ArenaColumns.All[ordinal];
            if (column is ArenaColumn.CellCapacity or ArenaColumn.Vector) { continue; }
            for (var index = 0; (index < layout.Size(column: column)); index++) {
                var row = layout.RowOf(column: column, index: index);
                var carried = ((rows is null)
                    ? ((row < 0) || (layout.HasColumn(row: row, column: column) && !layout[row].HostOwned))
                    : (rows.Contains(item: row) && layout.HasColumn(row: row, column: column)));
                if (carried) { yield return (column, index); }
            }
        }
    }

    private readonly record struct ArenaImage(ArenaNumberImage[] Numbers, ArenaTextImage[] Text, ArenaVisibilityImage[] Visibility, ArenaObservationImage[] Observations);

    // Reads each position into its typed list. A full snapshot omits a number at its column's default, which its
    // restore writes before the image; a scope keeps every number, since its restore checks a complete inventory.
    private ArenaImage CaptureImage(IEnumerable<(ArenaColumn Column, int Index)> positions, bool omitDefaults) {
        var numbers = new List<ArenaNumberImage>();
        var text = new List<ArenaTextImage>();
        var visibility = new List<ArenaVisibilityImage>();
        var observations = new List<ArenaObservationImage>();
        foreach (var (column, index) in positions) {
            switch (column) {
                case ArenaColumn.Text:
                    if (ReadReferenceRaw(column: column, index: index) is string value) { text.Add(item: new(Index: index, Value: value)); }
                    break;
                case ArenaColumn.Visibility:
                    if (ReadReferenceRaw(column: column, index: index) is StateVisibility policy) { visibility.Add(item: new(Index: index, Value: policy)); }
                    break;
                case ArenaColumn.Observation:
                    if (ReadReferenceRaw(column: column, index: index) is StateObservation observation) { observations.Add(item: new(Index: index, Value: observation)); }
                    break;
                default:
                    var raw = ReadNumberRaw(column: column, index: index);
                    if (!omitDefaults || (raw != (column == ArenaColumn.MemberKey ? -1L : 0L))) { numbers.Add(item: new(Column: column, Index: index, Value: raw)); }
                    break;
            }
        }
        return new(Numbers: numbers.ToArray(), Text: text.ToArray(), Visibility: visibility.ToArray(), Observations: observations.ToArray());
    }

    private void RequireSnapshotPosition(ArenaColumn column, int index) {
        if (!Enum.IsDefined(column) || (index < 0) || (index >= m_layout.Size(column: column))) { throw new InvalidOperationException(message: "The checkpoint carries an invalid column position."); }
        var row = m_layout.RowOf(column: column, index: index);
        if ((row >= 0) && (!m_layout.HasColumn(row: row, column: column) || m_layout[row].HostOwned)) { throw new InvalidOperationException(message: "The checkpoint writes an undeclared column."); }
    }

    private void RequireImagePosition(ArenaLayout layout, ArenaColumn column, int index, long number, object? reference, string what) {
        if (!TryAdmitImagePosition(layout: layout, column: column, row: layout.RowOf(column: column, index: index), number: number, reference: reference, reason: out var reason)) {
            throw new InvalidOperationException(message: $"The {what} {reason}.");
        }
    }

    // The one value admission of a raw column position crossing a snapshot boundary. A checkpoint's arena, a replay
    // scope's starting image and a retained undo turn each pass every position here before writing one, so no restore
    // writes a value another refuses. It checks what the position can hold under the layout it lands in (a member
    // key names an interned key, a link stays inside its row, a bit is a bit); a cell value's admission by its row's
    // declaration stays with the importer each door runs. A member key is the arena's own ordinal here: a door that
    // carries it by name resolves it first.
    private bool TryAdmitImagePosition(ArenaLayout layout, ArenaColumn column, int row, long number, object? reference, out string reason) {
        reason = string.Empty;
        if ((column is (ArenaColumn.Vector or ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation)) && (number != 0L)) {
            reason = $"carries a noncanonical number for column '{column}'";
            return false;
        }
        var payloadMatches = column switch {
            ArenaColumn.Text => (reference is (null or string)),
            ArenaColumn.Visibility => (reference is (null or StateVisibility)),
            ArenaColumn.Observation => (reference is (null or StateObservation)),
            _ => (reference is null),
        };
        if (!payloadMatches) {
            reason = $"carries a payload incompatible with column '{column}'";
            return false;
        }
        switch (column) {
            case ArenaColumn.Text when ((((string?)reference)?.Length ?? 0) > StateCapacity.MaxTextValueLength):
                reason = "carries text past the state text ceiling";
                return false;
            case ArenaColumn.Visibility:
                return StateVisibilityStorage.TryMeasure(value: (StateVisibility?)reference, bytes: out _, reason: out reason);
            case ArenaColumn.MemberKey when ((number < -1L) || (number >= m_keys.Count)):
                reason = "carries an invalid key ordinal";
                return false;
            case (ArenaColumn.Presence or ArenaColumn.ClockSet or ArenaColumn.LaneRoster) when (number is not (0L or 1L)):
                reason = "carries a non-bit value in a bit column";
                return false;
            case ArenaColumn.Behavior when ((number < byte.MinValue) || (number > byte.MaxValue) || !Enum.IsDefined(value: ((StateCellBehavior)((byte)number)))):
                reason = "carries an unknown cell behavior";
                return false;
        }
        if (row < 0) {
            return true;
        }
        if (IsLinkColumn(column: column) && ((number < 0L) || (number > layout[row].CellCapacity))) {
            reason = "carries a member link outside its row";
            return false;
        }
        if ((column == ArenaColumn.CellCapacity) && ((number < 0L) || (number > m_rows[row].CellCeiling))) {
            reason = "carries a reservation outside its row ceiling";
            return false;
        }
        if ((column == ArenaColumn.MemberCount) && ((number < 0L) || (number > layout[row].CellCapacity))) {
            reason = "carries a member count outside its row capacity";
            return false;
        }
        return true;
    }
}
