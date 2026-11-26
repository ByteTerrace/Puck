using Puck.Abstractions.Memory;
namespace Puck.World;

/// <summary>
/// One instance's portal-crossing latch: per <c>(placement, face, seat)</c>, whether that body was inside the face's
/// enterable region at the previous scan. A crossing fires on the EDGE into the region, so a body standing in a
/// doorway does not re-fire every step.
/// </summary>
/// <remarks>The latch keys on the region's own point test, never on the swept result: a body that passes fully
/// through in one step ends outside, fires once, and does not latch — so a repeat tunnelling crossing fires again
/// exactly like a repeat walk-through would. Only inside rows are retained; an outside scan removes its row.
/// <para>A scan observes every face for every body, and almost every observation is of a body outside a face it never
/// entered. Each seat's count of inside rows answers that case without hashing the row's key, so a scan pays a set
/// operation only for the seats that hold a latch. The count changes only where the set does, in
/// <see cref="Add"/> and <see cref="Remove"/>.</para></remarks>
public sealed class WorldPortalOccupancy {
    private readonly HashSet<(string PlacementId, string FaceName, int Seat)> m_inside = [];
    // The bodies a destination refused at a face, held from it until they stand clear. Host-side retry state: a
    // checkpoint carries none, so a restored body may ask once more.
    private readonly HashSet<(string PlacementId, string FaceName, int Seat)> m_refused = [];
    // The inside rows and the refusals each seat holds, indexed by seat; a seat past the end holds none.
    private int[] m_seatRows = [];
    private int[] m_seatRefusals = [];

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
    public void Forget(string placementId, string faceName, int seat) {
        if (Holds(seat: seat)) { _ = Remove(key: (placementId, faceName, seat)); }
    }
    /// <summary>Reports whether a body is currently latched inside a face's region.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    /// <returns><see langword="true"/> when the body is latched inside.</returns>
    public bool IsInside(string placementId, string faceName, int seat) =>
        (Holds(seat: seat) && m_inside.Contains(item: (placementId, faceName, seat)));
    /// <summary>Reports whether a body holds a latch at any face, which a scan reads before it observes the faces
    /// the body cannot be near: a body that holds none has nothing an outside observation would change.</summary>
    /// <param name="seat">The local seat index.</param>
    /// <returns><see langword="true"/> when the body is latched inside some face.</returns>
    public bool Holds(int seat) => (((uint)seat < (uint)m_seatRows.Length) && (m_seatRows[seat] != 0));
    /// <summary>Records a scan's result for one body at one face and reports whether that face fires.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    /// <param name="inside">Whether the body's current origin lies in the region.</param>
    /// <param name="crossed">Whether the body's swept segment met the region at all.</param>
    /// <returns><see langword="true"/> when this scan is an entry edge.</returns>
    public bool Observe(string placementId, string faceName, int seat, bool inside, bool crossed) {
        // Outside is absence. Observing an unrelated face must not retain a row for every traveller/face pair.
        var wasInside = (inside
            ? !Add(key: (placementId, faceName, seat))
            : (Holds(seat: seat) && Remove(key: (placementId, faceName, seat))));
        return !wasInside && crossed;
    }
    /// <summary>Restores every latched row from a previously captured set, replacing whatever this instance already
    /// holds.</summary>
    public void Restore(IReadOnlyList<(string PlacementId, string FaceName, int Seat)> rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);

        m_inside.Clear();
        m_refused.Clear();
        Array.Clear(array: m_seatRows);
        Array.Clear(array: m_seatRefusals);

        foreach (var row in rows) {
            _ = Add(key: (row.PlacementId, row.FaceName, row.Seat));
        }
    }
    /// <summary>Latches a body as already inside a face's region without firing it — what an ARRIVING traveler owes
    /// the door it lands at. Without it, a mapped pair whose isometry sets a traveler down on its counterpart's own
    /// threshold reads as a fresh entry edge and bounces the traveler straight back.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The local seat index.</param>
    public void SeedInside(string placementId, string faceName, int seat) =>
        _ = Add(key: (placementId, faceName, seat));
    /// <summary>Holds a body from a face whose destination refused it for good, until the scan sees it stand clear of the
    /// face and calls <see cref="Release"/>: a body pressed against the glass crosses the region's edge back and forth,
    /// and each entry would otherwise ask the destination again.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The body's index.</param>
    public void Refuse(string placementId, string faceName, int seat) {
        if (!m_refused.Add(item: (placementId, faceName, seat)) || (seat < 0)) { return; }
        ArrayCapacity.EnsureLength(array: ref m_seatRefusals, required: (seat + 1));
        m_seatRefusals[seat]++;
    }
    /// <summary>Reports whether a body is held from any face by a refusal, which a scan of the bodies that moved still
    /// visits: a held body standing clear of its face is released by the next scan that sees it.</summary>
    /// <param name="seat">The body's index.</param>
    /// <returns><see langword="true"/> when the body is held from some face.</returns>
    public bool HoldsRefusal(int seat) => (((uint)seat < (uint)m_seatRefusals.Length) && (m_seatRefusals[seat] != 0));
    /// <summary>Reports whether a body is held from a face by a refusal.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The body's index.</param>
    /// <returns><see langword="true"/> when the body is held.</returns>
    public bool IsRefused(string placementId, string faceName, int seat) => ((m_refused.Count > 0) && m_refused.Contains(item: (placementId, faceName, seat)));
    /// <summary>Lets a held body try the face again.</summary>
    /// <param name="placementId">The face's owning placement id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="seat">The body's index.</param>
    public void Release(string placementId, string faceName, int seat) {
        if ((m_refused.Count > 0) && m_refused.Remove(item: (placementId, faceName, seat)) && (seat >= 0)) { m_seatRefusals[seat]--; }
    }

    private bool Add((string PlacementId, string FaceName, int Seat) key) {
        if (!m_inside.Add(item: key)) { return false; }
        if (key.Seat >= 0) {
            ArrayCapacity.EnsureLength(array: ref m_seatRows, required: (key.Seat + 1));
            m_seatRows[key.Seat]++;
        }
        return true;
    }
    private bool Remove((string PlacementId, string FaceName, int Seat) key) {
        if (!m_inside.Remove(item: key)) { return false; }
        if (key.Seat >= 0) { m_seatRows[key.Seat]--; }
        return true;
    }
}
