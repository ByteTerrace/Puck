using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// One resolved world-space pose — the smallest shared vocabulary a camera rig, a placed prop, or a diegetic screen
/// needs to pose itself against something else: a position and an orientation, nothing else. Not a live reference (it
/// carries no id back to whatever produced it) — a snapshot for this tick, republished the next.
/// <see cref="Views.ISdfCameraRig.Resolve"/> consumes one; an <see cref="ISdfAnchorSource"/> resolves one by id.
/// </summary>
/// <param name="Position">The world-space position (or render-relative, matching whatever space the source and the
/// consumer both agree on — an anchor never carries its own space).</param>
/// <param name="Orientation">The world-space orientation. <see cref="Quaternion.Identity"/> for a pose that only ever
/// needs a position (many anchors — a room's spawn point, a static console screen — never rotate).</param>
public readonly record struct SdfAnchor(Vector3 Position, Quaternion Orientation);
/// <summary>
/// Resolves a stable integer anchor id to its current pose — the read half of the anchor contract. A camera rig, a
/// placed light, or any other consumer that wants to ride "whatever is publishing anchor 7 this tick" takes one of
/// these rather than a concrete host type, so it never needs to know whether the anchor is a walking avatar, a static
/// screen, or a placed stamp. Returns <see langword="false"/> for an id nothing currently publishes (a deleted
/// placement, a not-yet-ticked slot) — the caller's fallback (park the camera, skip the frame) is its own call.
/// </summary>
public interface ISdfAnchorSource {
    /// <summary>Resolves <paramref name="anchorId"/> to its pose this tick.</summary>
    /// <param name="anchorId">The anchor id, in the source's own id space.</param>
    /// <param name="anchor">The resolved pose, or <see langword="default"/> when the id resolves to nothing.</param>
    /// <returns><see langword="true"/> when the id is currently live.</returns>
    bool TryResolveAnchor(int anchorId, out SdfAnchor anchor);
}
