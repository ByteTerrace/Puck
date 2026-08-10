namespace Puck.World;

/// <summary>
/// One instance's portal-crossing latch: per <c>(placement, face, seat)</c>, whether that body was inside the face's
/// enterable region at the previous scan. A crossing fires on the EDGE into the region, so a body standing in a
/// doorway does not re-fire every step.
/// </summary>
/// <remarks>The latch keys on the region's own point test, never on the swept result: a body that passes fully
/// through in one step ends outside, fires once, and does not latch — so a repeat tunnelling crossing fires again
/// exactly like a repeat walk-through would.</remarks>
public sealed class WorldPortalOccupancy {
    private readonly Dictionary<(string PlacementId, string FaceName, int Seat), bool> m_inside = new();

    /// <summary>Records a scan's result for one body at one face and reports whether that face fires.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    /// <param name="inside">Whether the body's current origin lies in the region.</param>
    /// <param name="crossed">Whether the body's swept segment met the region at all.</param>
    /// <returns><see langword="true"/> when this scan is an entry edge.</returns>
    public bool Observe(string placementId, string faceName, int seat, bool inside, bool crossed) {
        var key = (placementId, faceName, seat);
        var wasInside = (m_inside.TryGetValue(key: key, value: out var previous) && previous);

        m_inside[key] = inside;

        return (!wasInside && crossed);
    }

    /// <summary>Latches a body as already inside a face's region without firing it — what an ARRIVING traveler owes
    /// the door it lands at. Without it, a mapped pair whose isometry sets a traveler down on its counterpart's own
    /// threshold reads as a fresh entry edge and bounces the traveler straight back.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    public void SeedInside(string placementId, string faceName, int seat) =>
        m_inside[(placementId, faceName, seat)] = true;

    /// <summary>Drops a body's latch at one face, so its next scan re-arms. An inactive seat carries no stale state
    /// forward: a seat that leaves mid-transit and later rejoins the same slot re-arms rather than firing on its
    /// very first step back.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    public void Forget(string placementId, string faceName, int seat) =>
        m_inside.Remove(key: (placementId, faceName, seat));

    /// <summary>Reports whether a body is currently latched inside a face's region.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    /// <returns><see langword="true"/> when the body is latched inside.</returns>
    public bool IsInside(string placementId, string faceName, int seat) =>
        (m_inside.TryGetValue(key: (placementId, faceName, seat), value: out var inside) && inside);
}
