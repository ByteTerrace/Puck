using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// One resolved world-space pose — the smallest shared vocabulary a camera rig, a placed prop, or a diegetic screen
/// needs to pose itself against something else: a position and an orientation, nothing else. Not a live reference (it
/// carries no id back to whatever produced it) — a snapshot for THIS tick, republished the next.
/// <see cref="Views.ISdfCameraRig.Resolve"/> consumes one; <see cref="SdfAnchorTable"/> is the sim-side registry that
/// produces them by name.
/// </summary>
/// <param name="Position">The world-space position (or render-relative, matching whatever space the publisher and the
/// consumer both agree on — an anchor never carries its own space; see <see cref="SdfAnchorTable"/>'s remarks).</param>
/// <param name="Orientation">The world-space orientation. <see cref="Quaternion.Identity"/> for a pose that only ever
/// needs a position (many anchors — a room's spawn point, a static console screen — never rotate).</param>
public readonly record struct SdfAnchor(Vector3 Position, Quaternion Orientation);

/// <summary>
/// Resolves a stable integer anchor id to its current pose — the READ half of the anchor contract. A camera rig, a
/// placed light, or any other consumer that wants to ride "whatever is publishing anchor 7 this tick" takes one of
/// these rather than a concrete host type, so it never needs to know whether the anchor is a walking avatar, a static
/// screen, or a placed stamp. Returns <see langword="false"/> for an id nothing currently publishes (a deleted
/// placement, a not-yet-ticked slot) — the caller's fallback (park the camera, skip the frame) is its own call.
/// </summary>
public interface ISdfAnchorSource {
    /// <summary>Resolves <paramref name="anchorId"/> to its pose this tick.</summary>
    /// <param name="anchorId">The stable anchor id (see <see cref="SdfAnchorTable.TryResolveId"/>).</param>
    /// <param name="anchor">The resolved pose, or <see langword="default"/> when the id resolves to nothing.</param>
    /// <returns><see langword="true"/> when the id is currently live.</returns>
    bool TryResolveAnchor(int anchorId, out SdfAnchor anchor);
}

/// <summary>
/// The sim-side anchor registry — the WRITE half of the anchor contract <see cref="ISdfAnchorSource"/> reads. A host
/// (the overworld's frame source, a future RTS scenario) owns one instance and, once per tick, republishes every
/// pose a consumer might want to ride: a player body, the room's spawn point, a console's screen face, a placed
/// camera's anchor stamp. Consumers never hold a reference to the thing that moved — they hold a NAME (resolved once
/// to a stable id) and re-resolve the pose through this table every frame, so an anchor's producer can change
/// identity entirely (a companion despawns and a different one takes its name) without the consumer noticing anything
/// but a pose jump.
/// <para>
/// LIFETIME: an id is stable for the table's own lifetime once assigned — <see cref="Publish"/> keys on the NAME, not
/// insertion order, so publishing "player.0" every tick always returns the same id, and a name that stops being
/// published simply stops resolving (<see cref="TryResolveAnchor"/> returns <see langword="false"/> for it) rather
/// than being reassigned to a new name. The table never shrinks or recycles an id — a run with a bounded, small
/// anchor count (players, consoles, a handful of placed eyes) never needs to; a future host publishing thousands of
/// short-lived anchors per tick would want a different design (out of scope for this table).
/// </para>
/// <para>
/// SPACE: the table imposes no coordinate space of its own — every anchor a host publishes must agree with every
/// consumer on what "the" space is for that run (the overworld anchors in its own render-relative space, subtracting
/// the room's spawn point the same way every other render-relative quantity does). Mixing spaces within one table is
/// a caller bug the table cannot catch.
/// </para>
/// <para>
/// PER-TICK REPUBLISH: nothing here is diffed or dirtied — a host calls <see cref="Publish"/> for every anchor it
/// wants live THIS tick, every tick, even when the pose hasn't moved (a static console screen republishes the same
/// pose every frame). This keeps the table a pure function of "what does the host say is live right now," with no
/// separate deregistration path to forget.
/// </para>
/// </summary>
public sealed class SdfAnchorTable : ISdfAnchorSource {
    private readonly Dictionary<string, int> m_idsByName = new(comparer: StringComparer.Ordinal);
    private readonly List<SdfAnchor> m_poses = [];
    private readonly List<bool> m_live = [];

    /// <summary>Marks every currently-assigned id as NOT live, ahead of this tick's <see cref="Publish"/> calls — the
    /// other half of the "stops publishing, stops resolving" contract (see the type remarks). A host calls this once
    /// at the start of its per-tick anchor pass, then <see cref="Publish"/>es every anchor it wants live this tick;
    /// any id nobody re-publishes stays marked not-live, so <see cref="TryResolveAnchor"/> correctly reports it gone
    /// (its NAME→id mapping is untouched — a later re-publish under the same name resumes the same id). Optional: a
    /// host that never removes an anchor (every name it ever publishes stays published for the run) need not call
    /// this at all — every id simply stays live forever.</summary>
    public void BeginTick() {
        for (var index = 0; (index < m_live.Count); index++) {
            m_live[index] = false;
        }
    }

    /// <summary>Publishes (or republishes) <paramref name="name"/>'s pose for this tick, returning its stable id. The
    /// first publish under a given name allocates a new id; every later publish under the SAME name (this tick or a
    /// future one) reuses it — a consumer that resolved the id once via <see cref="TryResolveId"/> keeps working
    /// across ticks without re-resolving the name. Call once per anchor per tick; the host's own tick loop is the
    /// natural place (see the type remarks).</summary>
    /// <param name="name">The anchor's stable name (e.g. <c>"player.0"</c>, <c>"world.spawn"</c>,
    /// <c>"console.2"</c>) — the ONE handle a consumer resolves against; case-sensitive, compared ordinally.</param>
    /// <param name="pose">This tick's pose.</param>
    /// <returns>The anchor's stable id.</returns>
    public int Publish(string name, in SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(name);

        if (!m_idsByName.TryGetValue(key: name, value: out var id)) {
            id = m_poses.Count;
            m_idsByName[name] = id;
            m_poses.Add(item: pose);
            m_live.Add(item: true);

            return id;
        }

        m_poses[id] = pose;
        m_live[id] = true;

        return id;
    }

    /// <summary>Resolves a published name to its stable id, without requiring the caller to know the pose (a rig
    /// binding a camera to <c>"player.0"</c> once at setup, then resolving the pose every frame through
    /// <see cref="TryResolveAnchor"/>).</summary>
    /// <param name="name">The anchor's name.</param>
    /// <param name="anchorId">The resolved id, or 0 when the name was never published.</param>
    /// <returns><see langword="true"/> when the name has ever been published (even if not live THIS tick — see the
    /// type remarks; a name that stops publishing keeps its id, it just stops resolving via
    /// <see cref="TryResolveAnchor"/>).</returns>
    public bool TryResolveId(string name, out int anchorId) {
        ArgumentNullException.ThrowIfNull(name);

        return m_idsByName.TryGetValue(key: name, value: out anchorId);
    }

    /// <inheritdoc/>
    public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
        if ((anchorId < 0) || (anchorId >= m_poses.Count) || !m_live[anchorId]) {
            anchor = default;

            return false;
        }

        anchor = m_poses[anchorId];

        return true;
    }
}
