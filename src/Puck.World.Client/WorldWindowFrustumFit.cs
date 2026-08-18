using System.Numerics;
using Puck.SdfVm.Views;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The window projection's SdfVm-dependent half: maps the viewer's eye through
/// <see cref="WorldWindowProjectionMath.MapPoint"/> (the border pair's isometry — pure, GPU-free, tested directly by
/// <c>WorldWindowProjectionMathLawTests</c>) and fits an <see cref="SdfAsymmetricFrustum"/> against the destination
/// aperture from that mapped position. Split into its own type, separate from the pure isometry math, because this
/// half needs <see cref="SdfAsymmetricFrustum"/> and <see cref="Puck.Abstractions.Cameras.CameraSnapshot"/> —
/// <c>Puck.SdfVm</c> dependencies <c>Puck.World.Protocol</c> (where the isometry lives, reachable by
/// <c>tests/Puck.World.Tests</c>) structurally may not carry (see docs/project-map.md's layering rules).
/// </summary>
public static class WorldWindowFrustumFit {
    /// <summary>Maps the viewer's eye through the border pair's isometry and fits an off-axis frustum against the
    /// destination aperture from that mapped position — the ONE call a window projection needs per produced frame.</summary>
    /// <param name="localEye">The viewer's eye, in source (local/boot) world space.</param>
    /// <param name="source">The source (local) face's own aperture geometry.</param>
    /// <param name="destination">The destination counterpart face's own aperture geometry.</param>
    /// <param name="camera">The fitted camera, apexed at the mapped eye, on success.</param>
    /// <param name="offset">The fitted frustum's tangent-space center offset (<see cref="Puck.SdfVm.SdfViewSnapshot.AsymmetricFrustumOffset"/>), on success.</param>
    /// <returns><see langword="true"/> when the mapped eye stands far enough in front of the destination aperture
    /// plane for a sound frustum to exist (see <see cref="SdfAsymmetricFrustum.MinEyeDepth"/>); <see langword="false"/>
    /// otherwise — the caller falls back to its ordinary default projection for this frame.</returns>
    public static bool TryFitWindow(Vector3 localEye, WorldFaceGeometry source, WorldFaceGeometry destination, out Puck.Abstractions.Cameras.CameraSnapshot camera, out Vector2 offset) {
        var mappedEye = WorldWindowProjectionMath.MapPoint(
            destination: destination,
            point: localEye,
            source: source
        );

        // -destination.Normal, not destination.Normal: WorldWindowProjectionMath.MapPoint's full 180° flip lands the
        // mapped eye on the destination aperture's OUTSIDE (the side its own Normal faces away from — see that
        // type's own remarks), so the direction that actually points toward the eye is the negated one.
        // SdfAsymmetricFrustum's Forward = -apertureNormal then resolves to +destination.Normal — into the room —
        // with that type never needing to know it is looking "backwards" relative to the aperture's own authored
        // outward face.
        if (!SdfAsymmetricFrustum.TryFit(
            eye: mappedEye,
            apertureOrigin: destination.Origin,
            apertureRight: destination.Right,
            apertureUp: destination.Up,
            apertureNormal: -destination.Normal,
            apertureHalfWidth: destination.HalfWidth,
            apertureHalfHeight: destination.HalfHeight,
            frustum: out var frustum
        )) {
            camera = default;
            offset = default;

            return false;
        }

        camera = frustum.ToCameraSnapshot(eye: mappedEye);
        offset = frustum.CenterOffset;

        return true;
    }
}
