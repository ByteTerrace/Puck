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
/// <c>Puck.World.Protocol</c> (where the isometry lives, reachable by <c>tests/Puck.World.Protocol.Tests</c>) structurally may
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

    // The two face frames a window screen's apertures are: the local face that claims the screen index, and the
    // counterpart its portal facet names in the destination.
    private static bool TryResolveFrames(WorldDefinition local, WorldDefinition destination, int screenIndex, out WorldFaceFrame source, out WorldFaceFrame counterpart, out WorldScreenSource? counterpartGlass) {
        ArgumentNullException.ThrowIfNull(argument: local);
        ArgumentNullException.ThrowIfNull(argument: destination);

        source = default;
        counterpart = default;
        counterpartGlass = null;

        if (!WorldFaceCatalog.For(definition: local).TryFindScreen(
            row: out var row,
            screenIndex: screenIndex
        )) {
            return false;
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

        source = row.Frame;
        counterpart = counterpartRow.Frame;
        counterpartGlass = ((counterpartRow.ScreenIndex >= 0) ? counterpartRow.Source : null);

        return true;
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
    /// <summary>Returns the display pixels a window's glass spans from the eye its camera was fitted to: the fitted
    /// frustum's height at one metre times the viewer's pixels a metre there. A fit carries the viewer's rays through
    /// the glass unchanged in angle, so the window's image shows the destination at the viewer's own resolution
    /// whatever size the image is rendered at, and its mesh demand measures projected sizes in this height
    /// (<see cref="Puck.SdfVm.SdfViewSnapshot.DemandHeight"/>).</summary>
    /// <param name="fitted">The window's fitted camera.</param>
    /// <param name="viewerPixelsPerMetre">The display pixels a metre covers one metre in front of the viewer
    /// (<see cref="WorldSeatViewports.ViewerPixelsPerMetre"/>).</param>
    /// <returns>The height in display pixels; zero when the viewer's resolution is unknown.</returns>
    public static float ShownHeight(in CameraSnapshot fitted, float viewerPixelsPerMetre) =>
        MathF.Max(x: 0f, y: ((2f * fitted.TanHalfFieldOfView) * viewerPixelsPerMetre));
    /// <summary>Returns the fit a window session renders through, against any viewer: a window inside a world another view
    /// renders fits to the camera that view renders with (a seat's presented there, or a window's one level up), as the
    /// boot world's windows fit to the presentation's viewer (<see cref="WorldSeatViewports.Viewer"/>). The fit's
    /// aperture is the part of the glass the viewer sees (<see cref="WorldPortalVisibility.Window"/>), so a glass that
    /// runs past the viewer's image or reaches its near plane renders only what can be seen.
    /// A transient gap yields <see langword="null"/>, the ordinary session projection for the frame.</summary>
    /// <param name="viewer">Reads the viewer's camera, in the local world's space, or <see langword="null"/> while there is
    /// none.</param>
    /// <param name="local">Reads the local document, whose face catalog seats the screen, or <see langword="null"/>.</param>
    /// <param name="destination">Reads the destination's document, as its session mirror last delivered it.</param>
    /// <param name="screen">Reads the screen row the window shows on, or <see langword="null"/>.</param>
    /// <returns>The fit.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static Func<WorldWindowFit?> FitFrom(Func<CameraSnapshot?> viewer, Func<WorldDefinition?> local, Func<WorldDefinition> destination, Func<WorldScreen?> screen) {
        ArgumentNullException.ThrowIfNull(argument: viewer);
        ArgumentNullException.ThrowIfNull(argument: local);
        ArgumentNullException.ThrowIfNull(argument: destination);
        ArgumentNullException.ThrowIfNull(argument: screen);

        // The apertures the fit last resolved, and what they were resolved from: a document's face catalog carries across
        // state-only changes, so a frame that asks again with the same catalogs, placements and screen index (every frame,
        // and several times a frame) fits against them without resolving again.
        ResolvedApertures? last = null;

        return () => {
            if (
                (local() is not { } document) ||
                (screen() is not { } row) ||
                (viewer() is not { } seen)
            ) {
                return null;
            }

            var target = destination();
            var apertures = Volatile.Read(location: ref last);

            if ((apertures is null) || !apertures.Matches(destination: target, local: document, screenIndex: row.Index)) {
                apertures = ResolvedApertures.Of(destination: target, local: document, screenIndex: row.Index);
                Volatile.Write(location: ref last, value: apertures);
            }

            var window = WorldPortalVisibility.Window(camera: in seen, glass: row);

            return (TryFitFromApertures(apertures: apertures, camera: out var camera, eye: seen.Position, screen: row, window: window)
                ? new WorldWindowFit(Camera: camera, Window: window)
                : null);
        };
    }
    // Fits a window screen's camera against an eye: the local face the screen shows on and its counterpart in the
    // destination, and the part of the glass its row draws (Glass) that the window names (WorldPortalVisibility.Window).
    // When the counterpart seats a screen, the camera's rays start past its glass (WorldPrototypeFacets.GlassSpan), so the
    // window never shows the destination's own glass it looks through, which shows a window of its own when the
    // counterpart is a return portal. False when a part does not resolve or the eye stands too near the glass's plane.
    private static bool TryFitFromApertures(ResolvedApertures apertures, Vector3 eye, WorldScreen screen, Vector4 window, out CameraSnapshot camera) {
        camera = default;

        if (!apertures.Resolved) {
            return false;
        }

        var counterpartFrame = apertures.Counterpart;
        var counterpartGlass = apertures.CounterpartGlass;
        var source = WorldFaceGeometry.FromFrame(frame: apertures.Source);
        var counterpart = WorldFaceGeometry.FromFrame(frame: counterpartFrame);
        var glass = Part(glass: Glass(screen: screen), window: window);

        if (!TryFitWindow(
            camera: out camera,
            destination: counterpart,
            glass: glass,
            localEye: eye,
            source: source
        )) {
            return false;
        }

        if (counterpartGlass is null) {
            return true;
        }

        // The camera's rays start on the mapped glass, this far in front of the counterpart's frame along its normal, and
        // run along the normal or against it; the counterpart's own glass spans [back, front] along the same normal.
        // Whichever side the camera looks through from, its rays start past that glass.
        var start = Vector3.Dot(
            vector1: (WorldWindowProjectionMath.MapPoint(
                destination: counterpart,
                point: glass.Origin,
                source: source
            ) - counterpart.Origin),
            vector2: counterpart.Normal
        );

        var (back, front) = WorldPrototypeFacets.GlassSpan(frame: counterpartFrame, source: counterpartGlass);
        var along = Vector3.Dot(
            vector1: camera.Forward,
            vector2: counterpart.Normal
        );
        var past = ((along < 0f)
            ? ((start - (back - GlassClearance)) / -along)
            : (((front + GlassClearance) - start) / MathF.Max(
                x: along,
                y: float.Epsilon
            )));

        if (past > 0f) {
            camera = (camera with { Near = (camera.Near + past) });
        }

        return true;
    }

    /// <summary>Returns the part of a glass a window names, as an aperture: the rectangle whose glass image coordinates
    /// span the window (<see cref="WorldPortalVisibility.Window"/>; <c>u</c> along the glass's right, <c>v</c> down its
    /// up), in the glass's plane and frame.</summary>
    /// <param name="glass">The whole glass (<see cref="Glass"/>).</param>
    /// <param name="window">The window: its left, top, right and bottom, each in [0, 1].</param>
    /// <returns>The part, the whole glass for the whole window.</returns>
    public static WorldFaceGeometry Part(WorldFaceGeometry glass, Vector4 window) {
        if (window == WorldPortalVisibility.WholeWindow) { return glass; }

        var right = Vector3.Normalize(value: glass.Right);
        var up = Vector3.Normalize(value: glass.Up);
        var across = ((window.X + window.Z) - 1f);
        var down = ((window.Y + window.W) - 1f);

        return glass with {
            HalfHeight = (glass.HalfHeight * (window.W - window.Y)),
            HalfWidth = (glass.HalfWidth * (window.Z - window.X)),
            Origin = ((glass.Origin + (right * (glass.HalfWidth * across))) - (up * (glass.HalfHeight * down))),
        };
    }

    /// <summary>How far past the counterpart's own glass a window's rays start, in world units.</summary>
    public const float GlassClearance = 0.01f;

    // A window screen's two face frames as TryResolveFrames resolves them, and everything it reads to do so: the local
    // document's face catalog and placements, the destination's face catalog, and the screen index.
    private sealed record ResolvedApertures(WorldFaceCatalog LocalCatalog, IReadOnlyList<WorldPlacement> LocalPlacements, WorldFaceCatalog DestinationCatalog, int ScreenIndex, bool Resolved, WorldFaceFrame Source, WorldFaceFrame Counterpart, WorldScreenSource? CounterpartGlass) {
        public bool Matches(WorldDefinition local, WorldDefinition destination, int screenIndex) => (
            (ScreenIndex == screenIndex) &&
            ReferenceEquals(objA: LocalPlacements, objB: local.Placements) &&
            ReferenceEquals(objA: LocalCatalog, objB: WorldFaceCatalog.For(definition: local)) &&
            ReferenceEquals(objA: DestinationCatalog, objB: WorldFaceCatalog.For(definition: destination))
        );
        public static ResolvedApertures Of(WorldDefinition local, WorldDefinition destination, int screenIndex) {
            var resolved = TryResolveFrames(
                counterpart: out var counterpart,
                counterpartGlass: out var counterpartGlass,
                destination: destination,
                local: local,
                screenIndex: screenIndex,
                source: out var source
            );

            return new(
                Counterpart: counterpart,
                CounterpartGlass: counterpartGlass,
                DestinationCatalog: WorldFaceCatalog.For(definition: destination),
                LocalCatalog: WorldFaceCatalog.For(definition: local),
                LocalPlacements: local.Placements,
                Resolved: resolved,
                ScreenIndex: screenIndex,
                Source: source
            );
        }
    }

    /// <summary>Maps the viewer's eye and the glass through the border pair's isometry and fits an off-axis frustum
    /// against the mapped glass from the mapped eye — the one call a window projection needs per produced frame.</summary>
    /// <param name="localEye">The viewer's eye, in the source world's space.</param>
    /// <param name="glass">The glass the window's screen draws (<see cref="Glass"/>), in the source world's space.</param>
    /// <param name="source">The source (local) face's own aperture geometry, one frame of the isometry.</param>
    /// <param name="destination">The destination counterpart face's own aperture geometry, the other frame.</param>
    /// <param name="camera">The fitted camera, apexed at the mapped eye, its
    /// <see cref="CameraSnapshot.FrustumOffset"/> the frustum's shear and its <see cref="CameraSnapshot.Near"/> the
    /// mapped glass's plane, so it sees only what lies beyond the aperture, on success.</param>
    /// <returns><see langword="true"/> when the eye stands far enough in front of the glass for a sound frustum to
    /// exist (see <see cref="SdfAsymmetricFrustum.MinEyeDepth"/>); <see langword="false"/> otherwise — the caller falls
    /// back to its ordinary default projection for this frame.</returns>
    public static bool TryFitWindow(Vector3 localEye, WorldFaceGeometry glass, WorldFaceGeometry source, WorldFaceGeometry destination, out CameraSnapshot camera) {
        var scale = WorldWindowProjectionMath.Scale(
            destination: destination,
            source: source
        );

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
            apertureHalfWidth: (glass.HalfWidth * scale),
            apertureHalfHeight: (glass.HalfHeight * scale),
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
/// <summary>A window session's fit for one frame: the camera it renders through and the part of its glass that camera's
/// image spans (<see cref="WorldPortalVisibility.Window"/>), which the glass maps the image onto
/// (<see cref="Puck.SdfVm.SdfViewSnapshot.ImageWindow"/>).</summary>
/// <param name="Camera">The fitted camera.</param>
/// <param name="Window">The part of the glass the camera's image spans: its left, top, right and bottom in the glass's
/// image coordinates.</param>
public readonly record struct WorldWindowFit(CameraSnapshot Camera, Vector4 Window);
