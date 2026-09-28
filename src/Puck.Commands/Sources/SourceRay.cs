using Puck.Abstractions.Cameras;
using Puck.Maths;

namespace Puck.Commands;

/// <summary>A ray in fixed point: the points <c>Origin + t·Direction</c> for <c>t ≥ 0</c>.</summary>
/// <param name="Origin">The ray's origin, in world units.</param>
/// <param name="Direction">The ray's direction; it need not be unit length.</param>
public readonly record struct SourceRay(FixedVector3 Origin, FixedVector3 Direction) {
    /// <summary>Returns the ray a camera casts through a point of its image: the direction
    /// <c>Forward + (2x − 1)·aspect·tan(fov/2)·Right + (1 − 2y)·tan(fov/2)·Up</c>, plus the trailing shear
    /// <c>offset.x·Right + offset.y·Up</c> of the camera's <see cref="CameraSnapshot.FrustumOffset"/>, the pinhole
    /// projection the SDF view pass's <c>cameraRayDirection</c> in <c>march/sdf-cone.hlsli</c> casts, left unnormalized.
    /// The ray starts on the camera's near plane: its origin is the camera's position advanced by
    /// <see cref="CameraSnapshot.Near"/> times that direction, whose forward component is one, so nothing nearer than
    /// the plane lies on the ray (a border window's ray starts on the glass). The camera's floats enter fixed point
    /// once, through <see cref="FixedVector3.FromVector3"/> and <see cref="FixedQ4816.FromDouble"/>.</summary>
    /// <param name="camera">The camera; its aspect ratio is the image's width over its height.</param>
    /// <param name="image">The point on the image, <c>x</c> right and <c>y</c> down, each in <c>[0, 1]</c> across the
    /// image.</param>
    /// <returns>The ray from the camera's near plane; from its position when the camera's near distance is zero.</returns>
    public static SourceRay Through(CameraSnapshot camera, FixedVector2 image) {
        var tangent = FixedQ4816.FromDouble(value: camera.TanHalfFieldOfView);
        var horizontal = (((((image.X + image.X) - FixedQ4816.One) * FixedQ4816.FromDouble(value: camera.AspectRatio)) * tangent) + FixedQ4816.FromDouble(value: camera.FrustumOffset.X));
        var vertical = (((FixedQ4816.One - (image.Y + image.Y)) * tangent) + FixedQ4816.FromDouble(value: camera.FrustumOffset.Y));
        var direction = ((FixedVector3.FromVector3(value: camera.Forward) + (FixedVector3.FromVector3(value: camera.Right) * horizontal)) + (FixedVector3.FromVector3(value: camera.Up) * vertical));

        return new SourceRay(
            Direction: direction,
            Origin: (FixedVector3.FromVector3(value: camera.Position) + (direction * FixedQ4816.FromDouble(value: camera.Near)))
        );
    }
}
