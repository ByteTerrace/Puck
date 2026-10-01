using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.World.Client;

/// <summary>
/// Whether a camera sees a screen's glass, which decides whether the session the glass shows renders this frame: a
/// portal face no level of a frame sees schedules nothing beneath it, so the counted cost of nesting follows what is
/// visible. The test is conservative, with a margin about the frustum, so a face entering the view renders before it is
/// shown: a face is unseen only when every corner of its sampled slab lies past one
/// plane of the camera's frustum (its near plane, or one of its four sides widened by <see cref="Margin"/>).
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

        var tangent = (camera.TanHalfFieldOfView * (1f + Margin));
        var horizontal = (camera.AspectRatio * tangent);
        var offset = camera.FrustumOffset;
        // One bit per frustum plane every corner so far lies outside of; a plane every corner lies outside culls the glass.
        var outside = 0b11111;

        for (var corner = 0; (corner < 8); corner++) {
            var point = (((origin + (((corner & 1) == 0) ? -right : right)) + (((corner & 2) == 0) ? -up : up)) + (((corner & 4) == 0) ? -thickness : thickness));
            var toward = (point - camera.Position);
            var depth = Vector3.Dot(
                vector1: toward,
                vector2: camera.Forward
            );
            var across = Vector3.Dot(
                vector1: toward,
                vector2: camera.Right
            );
            var height = Vector3.Dot(
                vector1: toward,
                vector2: camera.Up
            );
            var flags = 0;

            if (depth <= camera.Near) {
                flags |= 0b00001;
            }
            if (across > ((offset.X + horizontal) * depth)) {
                flags |= 0b00010;
            }
            if (across < ((offset.X - horizontal) * depth)) {
                flags |= 0b00100;
            }
            if (height > ((offset.Y + tangent) * depth)) {
                flags |= 0b01000;
            }
            if (height < ((offset.Y - tangent) * depth)) {
                flags |= 0b10000;
            }

            outside &= flags;
        }

        return (outside == 0);
    }
}
