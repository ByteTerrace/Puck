using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SdfVm.Views;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The window projection's SdfVm-dependent half: maps the viewer's eye through
/// <see cref="WorldWindowProjectionMath.MapPoint"/> (the border pair's isometry — pure, GPU-free, tested directly by
/// <c>WorldWindowProjectionMathLawTests</c>) and fits an <see cref="SdfAsymmetricFrustum"/> against the destination
/// aperture from that mapped position. Split into its own type, separate from the pure isometry math, because this
/// half needs <see cref="SdfAsymmetricFrustum"/> and <see cref="CameraSnapshot"/> — <c>Puck.SdfVm</c> dependencies
/// <c>Puck.World.Protocol</c> (where the isometry lives, reachable by <c>tests/Puck.World.Tests</c>) structurally may
/// not carry (see docs/project-map.md's layering rules).
/// </summary>
/// <remarks>
/// The fitted camera shows what a traveller standing at the eye would see through the door: the ray it casts through
/// the image point at the face's <c>(x, y)</c> is the isometry's image of the ray from the eye through that point of
/// the source face (<c>WorldWindowFrustumFitLawTests</c>). The isometry flips the aperture's Right and Normal together,
/// so the eye looks into the destination along the destination face's Normal, and the camera's right is the
/// destination face's Left.
/// </remarks>
public static class WorldWindowFrustumFit {
    /// <summary>Resolves the two apertures of a window screen: the local face that claims <paramref name="screenIndex"/>,
    /// and the counterpart its portal facet names, derived from the destination's document.</summary>
    /// <param name="local">The local document, whose face catalog seats the screen.</param>
    /// <param name="destination">The destination's document, as its session mirror last delivered it.</param>
    /// <param name="screenIndex">The derived-face screen index the window shows on.</param>
    /// <param name="source">The local face's aperture, on success.</param>
    /// <param name="counterpart">The counterpart face's aperture in the destination, on success.</param>
    /// <returns><see langword="true"/> when the screen index belongs to a face whose portal facet authors a mapped
    /// counterpart that the destination's document declares; <see langword="false"/> otherwise, including while the
    /// destination has not delivered a definition naming the counterpart.</returns>
    public static bool TryResolveApertures(WorldDefinition local, WorldDefinition destination, int screenIndex, out WorldFaceGeometry source, out WorldFaceGeometry counterpart) {
        ArgumentNullException.ThrowIfNull(argument: local);
        ArgumentNullException.ThrowIfNull(argument: destination);

        source = default;
        counterpart = default;

        foreach (var row in WorldFaceCatalog.For(definition: local).Rows) {
            if (row.ScreenIndex != screenIndex) {
                continue;
            }

            var face = ((WorldDefinitionRows.FindPlacement(
                placements: local.Placements,
                id: row.PlacementId
            ) is { } placement)
                ? WorldDefinitionRows.FindPlacementFace(
                    face: row.FaceName,
                    placement: placement
                )
                : null);

            if (
                (face?.Portal is not { Arrival: WorldPortalArrival.Mapped, Counterpart: { } named }) ||
                !WorldPortalCounterpart.TryParse(
                    counterpart: named,
                    face: out var counterpartFace,
                    placementId: out var counterpartPlacement
                ) ||
                !WorldFaceCatalog.For(definition: destination).TryFind(
                    faceName: counterpartFace,
                    placementId: counterpartPlacement,
                    row: out var counterpartRow
                )
            ) {
                return false;
            }

            source = WorldFaceGeometry.FromFrame(frame: row.Frame);
            counterpart = WorldFaceGeometry.FromFrame(frame: counterpartRow.Frame);

            return true;
        }

        return false;
    }
    /// <summary>Maps the viewer's eye through the border pair's isometry and fits an off-axis frustum against the
    /// destination aperture from that mapped position — the one call a window projection needs per produced frame.</summary>
    /// <param name="localEye">The viewer's eye, in the source world's space.</param>
    /// <param name="source">The source (local) face's own aperture geometry.</param>
    /// <param name="destination">The destination counterpart face's own aperture geometry.</param>
    /// <param name="camera">The fitted camera, apexed at the mapped eye, its
    /// <see cref="CameraSnapshot.FrustumOffset"/> the frustum's shear, on success.</param>
    /// <returns><see langword="true"/> when the mapped eye stands far enough in front of the destination aperture
    /// plane for a sound frustum to exist (see <see cref="SdfAsymmetricFrustum.MinEyeDepth"/>); <see langword="false"/>
    /// otherwise — the caller falls back to its ordinary default projection for this frame.</returns>
    public static bool TryFitWindow(Vector3 localEye, WorldFaceGeometry source, WorldFaceGeometry destination, out CameraSnapshot camera) {
        var mappedEye = WorldWindowProjectionMath.MapPoint(
            destination: destination,
            point: localEye,
            source: source
        );

        // The mapped eye stands on the side the destination's Normal faces away from (WorldWindowProjectionMath's
        // remarks), so the aperture as the eye sees it has Normal -destination.Normal and Right -destination.Right:
        // the same right-handed (Right, Up, Right x Up) the source face shows its viewer.
        if (!SdfAsymmetricFrustum.TryFit(
            eye: mappedEye,
            apertureOrigin: destination.Origin,
            apertureRight: -destination.Right,
            apertureUp: destination.Up,
            apertureNormal: -destination.Normal,
            apertureHalfWidth: destination.HalfWidth,
            apertureHalfHeight: destination.HalfHeight,
            frustum: out var frustum
        )) {
            camera = default;

            return false;
        }

        camera = frustum.ToCameraSnapshot(eye: mappedEye);

        return true;
    }
}
