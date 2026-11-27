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
        var zones = m_restrictingZones[rowOrdinal];
        // A cell with no policy of its own in no restricting zone is enclosed by every audience, so the row policy
        // answers for every cell.
        if ((zones.Length == 0) && !DeclaresCellVisibility(rowOrdinal: rowOrdinal)) { return true; }
        var cursor = 0;
        while (TryNextCell(rowOrdinal: rowOrdinal, cursor: ref cursor, key: out var key)) {
            Visit(lanes: 1L);
            if (!StateVisibility.Encloses(policy: Visibility(rowOrdinal: rowOrdinal, key: key), audience: audience)) { return false; }
            for (var index = 0; index < zones.Length; index++) {
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
    // The containing zones of each document row (StateCatalog.ContainingZones) that can restrict an audience, judged
    // from this arena's own rows and layout: the zones ReadAudienceEncloses walks come from the declarations whose
    // policies it reads. A catalog outlives a redeclaration that changes only a row's policy (StateCatalog.MatchesShape
    // does not compare it), so a list the catalog derived would keep a zone declared private since then out of the walk.
    // Every other containing zone encloses every audience, so skipping it answers the same.
    private int[][] RestrictingZones() {
        var zones = new int[m_rows.Count][];
        var restricting = new List<int>();

        for (var ordinal = 0; (ordinal < zones.Length); ordinal++) {
            restricting.Clear();
            foreach (var zone in m_catalog.ContainingZones(rowOrdinal: ordinal)) {
                if ((((uint)zone) < ((uint)m_rows.Count)) && (m_rows[zone] is { } declared) &&
                    StateVisibility.Restricts(policy: declared.Visibility, cellPolicies: DeclaresCellVisibility(rowOrdinal: zone))) {
                    restricting.Add(item: zone);
                }
            }
            zones[ordinal] = ((restricting.Count == 0) ? [] : restricting.ToArray());
        }
        return zones;
    }
}
