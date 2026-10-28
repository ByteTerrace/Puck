namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Validates live cross-row membership at the transaction boundary, without exporting rows.</summary>
    public bool TryValidateRelations(out string reason) {
        return TryValidateRelations(changedRows: default, reason: out reason);
    }
    /// <summary>Validates only relations whose member row or token domain changed. An empty span validates all.</summary>
    public bool TryValidateRelations(ReadOnlySpan<bool> changedRows, out string reason) {
        reason = string.Empty;
        for (var ordinal = 0; (ordinal < m_rows.Count); ordinal++) {
            ref readonly var layout = ref m_layout[ordinal];

            if (layout.DomainOrdinal < 0) { continue; }
            if (!changedRows.IsEmpty && !changedRows[ordinal] && !changedRows[layout.DomainOrdinal]) { continue; }
            var cursor = 0;

            while (TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: ordinal)) {
                if (!TryRead(layout.DomainOrdinal, key, out _)) {
                    reason = $"state row '{m_rows[ordinal].Name}': key '{m_keys[key]}' is outside token domain '{m_rows[layout.DomainOrdinal].Name}'";
                    return false;
                }
                if ((layout.Shape == RowShape.Ordered) && TryRead(key: key, rowOrdinal: ordinal, value: out var value) && (value.Raw != 1L)) {
                    reason = "an ordered zone's membership must be true"; return false;
                }
                if ((layout.ValueTopology is { } topology) && TryRead(key: key, rowOrdinal: ordinal, value: out var position) && (((ulong)position.Raw) >= ((ulong)topology.CellCount))) {
                    reason = "a token position is outside its valuesFrom topology"; return false;
                }
            }
        }
        return true;
    }
}
