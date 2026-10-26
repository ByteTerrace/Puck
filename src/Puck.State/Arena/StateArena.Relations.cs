namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Validates live cross-row membership at the transaction boundary, without exporting rows.</summary>
    public bool TryValidateRelations(out string reason) {
        reason = string.Empty;
        for (var ordinal = 0; ordinal < m_rows.Count; ordinal++) {
            ref readonly var layout = ref m_layout[ordinal];
            if (layout.DomainOrdinal < 0) { continue; }
            var cursor = 0;
            while (TryNextCell(ordinal, ref cursor, out var key)) {
                if (!TryRead(layout.DomainOrdinal, key, out _)) {
                    reason = $"state row '{m_rows[ordinal].Name}': key '{m_keys[key]}' is outside token domain '{m_rows[layout.DomainOrdinal].Name}'";
                    return false;
                }
                if (layout.Shape == RowShape.Ordered && TryRead(ordinal, key, out var value) && value.Raw != 1L) {
                    reason = "an ordered zone's membership must be true"; return false;
                }
            }
        }
        return true;
    }
}
