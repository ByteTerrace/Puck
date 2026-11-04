namespace Puck.World;

/// <summary>
/// One instance's portal-crossing latch: per <c>(placement, face, seat)</c>, whether that body was inside the face's
/// enterable region at the previous scan. A crossing fires on the EDGE into the region, so a body standing in a
/// doorway does not re-fire every step.
/// </summary>
/// <remarks>The latch keys on the region's own point test, never on the swept result: a body that passes fully
/// through in one step ends outside, fires once, and does not latch — so a repeat tunnelling crossing fires again
/// exactly like a repeat walk-through would. Only inside rows are retained; an outside scan removes its row.</remarks>
public sealed class WorldPortalOccupancy {
    private readonly HashSet<(string PlacementId, string FaceName, int Seat)> m_inside = [];

    /// <summary>Captures every inside row in ordinal placement/face/seat order. Snapshot order depends on membership,
    /// never on the set's freed slots or whether the same membership was restored from a checkpoint.</summary>
    public IReadOnlyList<(string PlacementId, string FaceName, int Seat)> Capture() {
        var rows = m_inside.ToArray();
        Array.Sort(rows, static (left, right) => {
            var order = StringComparer.Ordinal.Compare(left.PlacementId, right.PlacementId);
            if (order != 0) { return order; }
            order = StringComparer.Ordinal.Compare(left.FaceName, right.FaceName);
            return order != 0 ? order : left.Seat.CompareTo(right.Seat);
        });
        return rows;
    }
    /// <summary>Drops a body's latch at one face, so its next scan re-arms. An inactive seat carries no stale state
    /// forward: a seat that leaves mid-transit and later rejoins the same slot re-arms rather than firing on its
    /// very first step back.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    public void Forget(string placementId, string faceName, int seat) =>
        m_inside.Remove(item: (placementId, faceName, seat));
    /// <summary>Reports whether a body is currently latched inside a face's region.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    /// <returns><see langword="true"/> when the body is latched inside.</returns>
    public bool IsInside(string placementId, string faceName, int seat) =>
        m_inside.Contains(item: (placementId, faceName, seat));
    /// <summary>Records a scan's result for one body at one face and reports whether that face fires.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    /// <param name="inside">Whether the body's current origin lies in the region.</param>
    /// <param name="crossed">Whether the body's swept segment met the region at all.</param>
    /// <returns><see langword="true"/> when this scan is an entry edge.</returns>
    public bool Observe(string placementId, string faceName, int seat, bool inside, bool crossed) {
        var key = (placementId, faceName, seat);
        // Outside is absence. Observing an unrelated face must not retain a row for every traveller/face pair.
        var wasInside = (inside ? !m_inside.Add(item: key) : m_inside.Remove(item: key));
        return !wasInside && crossed;
    }
    /// <summary>Restores every latched row from a previously captured set, replacing whatever this instance already
    /// holds.</summary>
    public void Restore(IReadOnlyList<(string PlacementId, string FaceName, int Seat)> rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);

        m_inside.Clear();

        foreach (var row in rows) {
            m_inside.Add(item: (row.PlacementId, row.FaceName, row.Seat));
        }
    }
    /// <summary>Latches a body as already inside a face's region without firing it — what an ARRIVING traveler owes
    /// the door it lands at. Without it, a mapped pair whose isometry sets a traveler down on its counterpart's own
    /// threshold reads as a fresh entry edge and bounces the traveler straight back.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    public void SeedInside(string placementId, string faceName, int seat) =>
        m_inside.Add(item: (placementId, faceName, seat));
}
