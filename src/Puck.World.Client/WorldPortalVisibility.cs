using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.World.Client;

/// <summary>
/// Whether a camera sees a screen's glass, which decides whether the session the glass shows renders this frame: a
/// portal face no level of a frame sees schedules nothing beneath it, so the counted cost of nesting follows what is
/// visible. The test is conservative, with a margin about the frustum, so a face entering the view renders before it is
/// shown: a face is unseen only when its whole sampled slab lies past one plane of the camera's frustum (its near
/// plane, or one of its four sides widened by <see cref="Margin"/>; <see cref="CameraSnapshot.MaySee"/>).
/// Presentation only: what a face shows never reaches simulation state.
/// </summary>
public static class WorldPortalVisibility {
    /// <summary>The fraction the frustum's sides are widened by before a face is judged outside them.</summary>
    public const float Margin = 0.1f;

    /// <summary>Returns whether a camera sees a screen's glass.</summary>
    /// <param name="camera">The camera, in the screen's world.</param>
    /// <param name="glass">The screen row whose face is the glass.</param>
    /// <returns><see langword="false"/> when the whole sampled slab lies outside
    /// one plane of the camera's widened frustum; otherwise <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public static bool Sees(in CameraSnapshot camera, WorldScreen glass) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        var (origin, right, up, thickness) = Slab(glass);

        return camera.MaySee(
            center: origin,
            halfX: right,
            halfY: up,
            halfZ: thickness,
            margin: Margin
        );
    }
    /// <summary>The fraction of its consumer's image, on each axis, a window out of sight renders at while its world
    /// still bakes or lands its meshes: the least that keeps it rendering, since a world's meshes arrive only in frames
    /// that render one of its views.</summary>
    public const double ArrivalShare = 0.0625;
    /// <summary>Returns a glass's reach, in metres: the half-extents of its slab along its three axes together, which a
    /// lead adds to the authored distance (<see cref="WorldViewDefaults.PortalLead"/>), so a large glass warms as early
    /// from its edge as a small one does from its middle.</summary>
    /// <param name="glass">The screen row whose face is the glass.</param>
    /// <returns>The reach.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public static float ReachOf(WorldScreen glass) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        var (_, right, up, thickness) = Slab(glass);

        return ((right.Length() + up.Length()) + thickness.Length());
    }
    /// <summary>Returns the unit normal of a glass's face, pointing to the side the glass faces (the side its slab's
    /// front stands toward: <c>Right</c> × <c>Up</c>, as <see cref="WorldScreenStamper"/> seats the slab behind it).</summary>
    /// <param name="glass">The screen row whose face is the glass.</param>
    /// <returns>The normal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public static Vector3 FacingOf(WorldScreen glass) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        return Vector3.Normalize(value: Vector3.Cross(vector1: Vector3.Normalize(value: glass.Right), vector2: Vector3.Normalize(value: glass.Up)));
    }
    /// <summary>Returns whether a glass out of a camera's sight leads by line where no walk can be measured: the camera
    /// stands on the side the glass faces, and within <paramref name="lead"/> plus the glass's reach
    /// (<see cref="ReachOf"/>) of the glass's face in a straight line.</summary>
    /// <param name="camera">The consumer's camera.</param>
    /// <param name="glass">The screen row the session shows on.</param>
    /// <param name="lead">The authored lead, in metres (<see cref="WorldViewDefaults.PortalLead"/>).</param>
    /// <param name="metres">The straight distance from the camera to the face's centre, in metres.</param>
    /// <returns>Whether the glass leads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public static bool LeadsInLine(in CameraSnapshot camera, WorldScreen glass, float lead, out float metres) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        var origin = (Vector3)glass.Origin;

        metres = Vector3.Distance(value1: camera.Position, value2: origin);
        return ((Vector3.Dot(vector1: (camera.Position - origin), vector2: FacingOf(glass: glass)) >= 0f) && (metres <= (lead + ReachOf(glass: glass))));
    }
    /// <summary>The fraction the frustum's sides are widened by when the visible part of a glass is found
    /// (<see cref="Window"/>, <see cref="Coverage"/>): room for the reader's texture filter at the image's edge.</summary>
    public const float VisibleMargin = 0.02f;
    /// <summary>The step, as a share of the glass on each axis, a visible window's edges are rounded outward to, so the
    /// floating-point noise of a still camera never moves the window.</summary>
    public const float WindowStep = (1f / 256f);

    /// <summary>Returns the fraction of a camera's image the visible part of a screen's glass spans on each axis: the
    /// extent, in the image, of the glass's slab clipped to the camera's near plane and to its sides widened by
    /// <see cref="VisibleMargin"/>, so a session whose frustum is fitted to that part (<see cref="Window"/>) and rendered
    /// at that fraction of the camera's extent draws about one texel for each pixel the glass covers, however near the
    /// glass stands or however far it runs past the image's edges.</summary>
    /// <param name="camera">The camera, in the screen's world.</param>
    /// <param name="glass">The screen row whose face is the glass.</param>
    /// <returns>The fractions of the image's width and height, each in [0, 1]; zero for a glass the camera does not
    /// see.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public static (double Width, double Height) Coverage(in CameraSnapshot camera, WorldScreen glass) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        Span<Vector3> visible = stackalloc Vector3[VisibleCapacity];
        var count = Visible(camera: in camera, glass: glass, visible: visible);
        var low = new Vector2(value: float.PositiveInfinity);
        var high = new Vector2(value: float.NegativeInfinity);

        for (var index = 0; (index < count); index++) {
            // A clipped point lies on or in front of the near plane, so it projects.
            if (!camera.TryProject(depth: out _, ndc: out var projected, world: visible[index])) { continue; }
            low = Vector2.Min(value1: low, value2: projected);
            high = Vector2.Max(value1: high, value2: projected);
        }
        if (!(high.X >= low.X)) { return (Width: 0.0, Height: 0.0); }

        // Normalized device coordinates span two units on each axis.
        return (
            Width: Math.Clamp(value: (0.5 * (Math.Min(val1: high.X, val2: 1f) - Math.Max(val1: low.X, val2: -1f))), min: 0.0, max: 1.0),
            Height: Math.Clamp(value: (0.5 * (Math.Min(val1: high.Y, val2: 1f) - Math.Max(val1: low.Y, val2: -1f))), min: 0.0, max: 1.0)
        );
    }
    /// <summary>Returns the part of a screen's glass a camera sees, as the rectangle of the glass's own image coordinates
    /// it spans (<c>u</c> from the glass's left edge to its right, <c>v</c> from its top to its bottom, each in [0, 1], as
    /// the screen shading reads them): the glass's slab clipped to the camera's near plane and to its sides widened by
    /// <see cref="VisibleMargin"/>, its bounds rounded outward to <see cref="WindowStep"/>. A window's session fits its
    /// frustum to that rectangle (<see cref="WorldWindowFrustumFit"/>) and the glass maps its image there, so only what
    /// can be seen is rendered. The whole glass where all of it is seen, or none of it.</summary>
    /// <param name="camera">The camera looking at the glass, in the screen's world.</param>
    /// <param name="glass">The screen row whose face is the glass.</param>
    /// <returns>The rectangle: its left, top, right and bottom.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public static Vector4 Window(in CameraSnapshot camera, WorldScreen glass) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        Span<Vector3> visible = stackalloc Vector3[VisibleCapacity];
        var count = Visible(camera: in camera, glass: glass, visible: visible);

        if (count == 0) { return WholeWindow; }

        var right = Vector3.Normalize(value: glass.Right);
        var up = Vector3.Normalize(value: glass.Up);
        var origin = (Vector3)glass.Origin;
        var low = new Vector2(value: float.PositiveInfinity);
        var high = new Vector2(value: float.NegativeInfinity);

        for (var index = 0; (index < count); index++) {
            var local = (visible[index] - origin);
            var uv = new Vector2(
                x: (0.5f + ((0.5f * Vector3.Dot(vector1: local, vector2: right)) / glass.HalfWidth)),
                y: (0.5f - ((0.5f * Vector3.Dot(vector1: local, vector2: up)) / glass.HalfHeight))
            );

            low = Vector2.Min(value1: low, value2: uv);
            high = Vector2.Max(value1: high, value2: uv);
        }

        var window = new Vector4(
            x: Math.Clamp(value: (MathF.Floor(x: (low.X / WindowStep)) * WindowStep), min: 0f, max: 1f),
            y: Math.Clamp(value: (MathF.Floor(x: (low.Y / WindowStep)) * WindowStep), min: 0f, max: 1f),
            z: Math.Clamp(value: (MathF.Ceiling(x: (high.X / WindowStep)) * WindowStep), min: 0f, max: 1f),
            w: Math.Clamp(value: (MathF.Ceiling(x: (high.Y / WindowStep)) * WindowStep), min: 0f, max: 1f)
        );

        return (((window.Z - window.X) >= WindowStep) && ((window.W - window.Y) >= WindowStep)) ? window : WholeWindow;
    }
    /// <summary>Gets the window that shows the whole glass (<see cref="Window"/>).</summary>
    public static Vector4 WholeWindow => new(x: 0f, y: 0f, z: 1f, w: 1f);

    // The most points one face of the slab keeps through the five planes it is clipped by, and the points all six keep.
    private const int FacePoints = (4 + CameraSnapshot.BoundingPlaneCount);
    private const int VisibleCapacity = (6 * FacePoints);

    // Writes the points bounding what a camera sees of a glass's slab: each of its six faces clipped to the camera's near
    // plane and its widened sides (CameraSnapshot.BoundingPlanes; a point inside has dot(normal, point) + W at or under
    // zero). Returns how many it wrote, zero for a slab the camera does not see.
    private static int Visible(in CameraSnapshot camera, WorldScreen glass, Span<Vector3> visible) {
        Span<Vector4> planes = stackalloc Vector4[CameraSnapshot.BoundingPlaneCount];
        Span<Vector3> face = stackalloc Vector3[FacePoints];
        Span<Vector3> clipped = stackalloc Vector3[FacePoints];
        var (center, right, up, thickness) = Slab(glass);
        var count = 0;

        camera.BoundingPlanes(margin: VisibleMargin, planes: planes);
        for (var side = 0; (side < 6); side++) {
            // The face's normal axis and the two axes across it.
            var (axis, across, along) = ((side >> 1) switch {
                0 => (thickness, right, up),
                1 => (right, up, thickness),
                _ => (up, thickness, right),
            });
            var middle = (center + (((side & 1) == 0) ? axis : -axis));

            face[0] = ((middle - across) - along);
            face[1] = ((middle + across) - along);
            face[2] = ((middle + across) + along);
            face[3] = ((middle - across) + along);

            var points = 4;

            for (var plane = 0; ((plane < planes.Length) && (points > 0)); plane++) {
                points = ClipAgainst(clipped: clipped, plane: planes[plane], points: face[..points]);
                clipped[..points].CopyTo(destination: face);
            }
            face[..points].CopyTo(destination: visible[count..]);
            count += points;
        }

        return count;
    }
    // Clips a convex polygon to the side of a plane where dot(normal, point) + W is at or under zero (Sutherland and
    // Hodgman), writing the kept polygon; returns its point count.
    private static int ClipAgainst(ReadOnlySpan<Vector3> points, Vector4 plane, Span<Vector3> clipped) {
        var normal = new Vector3(x: plane.X, y: plane.Y, z: plane.Z);
        var kept = 0;

        for (var index = 0; (index < points.Length); index++) {
            var current = points[index];
            var next = points[((index + 1) % points.Length)];
            var at = (Vector3.Dot(vector1: normal, vector2: current) + plane.W);
            var to = (Vector3.Dot(vector1: normal, vector2: next) + plane.W);

            if (at <= 0f) { clipped[kept++] = current; }
            if ((at <= 0f) != (to <= 0f)) { clipped[kept++] = Vector3.Lerp(value1: current, value2: next, amount: (at / (at - to))); }
        }

        return kept;
    }

    // Visibility and coverage bound the same sampled solid. WorldScreenStamper seats its front at Origin and its
    // back one full depth behind; the back and sides sample the image as well as the front.
    internal static (Vector3 Origin, Vector3 Right, Vector3 Up, Vector3 Thickness) Slab(WorldScreen glass) {
        var right = (Vector3.Normalize(value: glass.Right) * glass.HalfWidth);
        var up = (Vector3.Normalize(value: glass.Up) * glass.HalfHeight);
        var thickness = (FacingOf(glass: glass) * glass.HalfDepth);

        return (((Vector3)glass.Origin) - thickness, right, up, thickness);
    }
}
