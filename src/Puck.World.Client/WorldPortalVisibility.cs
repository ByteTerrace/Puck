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

        var right = (Vector3.Normalize(value: glass.Right) * glass.HalfWidth);
        var up = (Vector3.Normalize(value: glass.Up) * glass.HalfHeight);
        var normal = Vector3.Normalize(value: Vector3.Cross(
            vector1: right,
            vector2: up
        ));
        var thickness = (normal * glass.HalfDepth);
        // WorldScreenStamper seats the slab behind Origin. Its back and sides sample the image too.
        var origin = (((Vector3)glass.Origin) - thickness);

        return camera.MaySee(
            center: origin,
            halfX: right,
            halfY: up,
            halfZ: thickness,
            margin: Margin
        );
    }
    /// <summary>The fraction of its consumer's image, on each axis, a window out of sight renders at while its world
    /// still bakes: the least that keeps it rendering, since a world bakes only in frames that render one of its
    /// views.</summary>
    public const double BakingShare = 0.0625;
    /// <summary>Returns the fraction of a camera's image a screen's glass spans on each axis: the extent of its face's
    /// projected bounds, unclipped by the image's edges, so a session rendered at that fraction of the camera's extent
    /// draws one texel for each pixel the glass covers wherever it lies. A face reaching to or behind the camera's near
    /// plane spans the whole image.</summary>
    /// <param name="camera">The camera, in the screen's world.</param>
    /// <param name="glass">The screen row whose face is the glass.</param>
    /// <returns>The fractions of the image's width and height, each in [0, 1].</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public static (double Width, double Height) Coverage(in CameraSnapshot camera, WorldScreen glass) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        var right = (Vector3.Normalize(value: glass.Right) * glass.HalfWidth);
        var up = (Vector3.Normalize(value: glass.Up) * glass.HalfHeight);
        var origin = ((Vector3)glass.Origin);
        var low = new Vector2(value: float.PositiveInfinity);
        var high = new Vector2(value: float.NegativeInfinity);

        for (var corner = 0; (corner < 4); corner++) {
            if (
                !camera.TryProject(
                    depth: out var depth,
                    ndc: out var projected,
                    world: ((origin + (((corner & 1) == 0) ? -right : right)) + (((corner & 2) == 0) ? -up : up))
                ) ||
                (depth <= camera.Near)
            ) {
                return (Width: 1.0, Height: 1.0);
            }

            low = Vector2.Min(value1: low, value2: projected);
            high = Vector2.Max(value1: high, value2: projected);
        }

        // Normalized device coordinates span two units on each axis.
        return (
            Width: Math.Clamp(value: (0.5 * (high.X - low.X)), min: 0.0, max: 1.0),
            Height: Math.Clamp(value: (0.5 * (high.Y - low.Y)), min: 0.0, max: 1.0)
        );
    }
}
