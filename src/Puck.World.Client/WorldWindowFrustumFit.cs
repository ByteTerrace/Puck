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
/// the image point at the glass's <c>(x, y)</c> is the isometry's image of the ray from the eye through that point of
/// the glass the screen draws (<c>WorldWindowFrustumFitLawTests</c>). The frustum's aperture is the glass itself,
/// mapped axis by axis through the door's isometry, so the image fills exactly the rectangle the screen shows, and the
/// mapped frame keeps the glass's handedness.
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
    /// <summary>Returns the glass a screen row draws, as an aperture: its rectangle, and its Normal
    /// <c>Right × Up</c>, toward the side it is seen from.</summary>
    /// <param name="screen">The screen row the window shows on.</param>
    /// <returns>The glass's aperture geometry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static WorldFaceGeometry Glass(WorldScreen screen) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        Vector3 right = screen.Right;
        Vector3 up = screen.Up;

        return new WorldFaceGeometry(
            HalfHeight: screen.HalfHeight,
            HalfWidth: screen.HalfWidth,
            Normal: Vector3.Normalize(value: Vector3.Cross(
                vector1: right,
                vector2: up
            )),
            Origin: screen.Origin,
            Right: right,
            Up: up
        );
    }
    /// <summary>Maps the viewer's eye and the glass through the border pair's isometry and fits an off-axis frustum
    /// against the mapped glass from the mapped eye — the one call a window projection needs per produced frame.</summary>
    /// <param name="localEye">The viewer's eye, in the source world's space.</param>
    /// <param name="glass">The glass the window's screen draws (<see cref="Glass"/>), in the source world's space.</param>
    /// <param name="source">The source (local) face's own aperture geometry, one frame of the isometry.</param>
    /// <param name="destination">The destination counterpart face's own aperture geometry, the other frame.</param>
    /// <param name="camera">The fitted camera, apexed at the mapped eye, its
    /// <see cref="CameraSnapshot.FrustumOffset"/> the frustum's shear, on success.</param>
    /// <returns><see langword="true"/> when the eye stands far enough in front of the glass for a sound frustum to
    /// exist (see <see cref="SdfAsymmetricFrustum.MinEyeDepth"/>); <see langword="false"/> otherwise — the caller falls
    /// back to its ordinary default projection for this frame.</returns>
    public static bool TryFitWindow(Vector3 localEye, WorldFaceGeometry glass, WorldFaceGeometry source, WorldFaceGeometry destination, out CameraSnapshot camera) {
        if (!SdfAsymmetricFrustum.TryFit(
            eye: WorldWindowProjectionMath.MapPoint(
                destination: destination,
                point: localEye,
                source: source
            ),
            apertureOrigin: WorldWindowProjectionMath.MapPoint(
                destination: destination,
                point: glass.Origin,
                source: source
            ),
            apertureRight: WorldWindowProjectionMath.MapVector(
                destination: destination,
                source: source,
                vector: glass.Right
            ),
            apertureUp: WorldWindowProjectionMath.MapVector(
                destination: destination,
                source: source,
                vector: glass.Up
            ),
            apertureNormal: WorldWindowProjectionMath.MapVector(
                destination: destination,
                source: source,
                vector: glass.Normal
            ),
            apertureHalfWidth: glass.HalfWidth,
            apertureHalfHeight: glass.HalfHeight,
            frustum: out var frustum
        )) {
            camera = default;

            return false;
        }

        camera = frustum.ToCameraSnapshot(eye: WorldWindowProjectionMath.MapPoint(
            destination: destination,
            point: localEye,
            source: source
        ));

        return true;
    }
}
