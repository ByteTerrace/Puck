namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Whether every live cell read from a row may influence a result visible to the supplied audience.
    /// Row, current cell, and containing ordered-zone policies all intersect. Reader-row identities are compared
    /// by declaration, so later changes to a live reader list cannot silently widen a stored result.</summary>
    /// <param name="rowOrdinal">The source row's catalog ordinal.</param>
    /// <param name="audience">The destination's audience; null means public.</param>
    /// <returns>True when every applicable source restriction encloses the destination audience.</returns>
    public bool ReadAudienceEncloses(int rowOrdinal, StateVisibility? audience) {
        if ((uint)rowOrdinal >= (uint)m_rows.Count) { return false; }
        var row = m_rows[rowOrdinal];
        Visit(lanes: 1L);
        if (!StateVisibility.Encloses(policy: row.Visibility, audience: audience)) { return false; }
        var zones = m_catalog.ContainingZones(rowOrdinal: rowOrdinal);
        var cursor = 0;
        while (TryNextCell(rowOrdinal: rowOrdinal, cursor: ref cursor, key: out var key)) {
            Visit(lanes: 1L);
            if (!StateVisibility.Encloses(policy: Visibility(rowOrdinal: rowOrdinal, key: key), audience: audience)) { return false; }
            for (var index = 0; index < zones.Count; index++) {
                var ordinal = zones[index];
                var zone = m_rows[ordinal];
                Visit(lanes: 1L);
                if (!TryCellSlot(rowOrdinal: ordinal, key: key, slot: out _)) { continue; }
                Visit(lanes: 2L);
                if (!StateVisibility.Encloses(policy: zone.Visibility, audience: audience) ||
                    !StateVisibility.Encloses(policy: Visibility(rowOrdinal: ordinal, key: key), audience: audience)) { return false; }
            }
        }
        return true;
    }
}
