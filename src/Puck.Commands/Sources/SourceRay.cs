using Puck.Abstractions.Cameras;
using Puck.Maths;

namespace Puck.Commands;

/// <summary>A ray in fixed point: the points <c>Origin + t·Direction</c> for <c>t ≥ 0</c>.</summary>
/// <param name="Origin">The ray's origin, in world units.</param>
/// <param name="Direction">The ray's direction; it need not be unit length.</param>
public readonly record struct SourceRay(FixedVector3 Origin, FixedVector3 Direction) {
    /// <summary>Returns the ray a camera casts through a point of its image: the direction
    /// <c>Forward + (2x − 1)·aspect·tan(fov/2)·Right + (1 − 2y)·tan(fov/2)·Up</c>, the pinhole projection the SDF view
    /// pass's <c>cameraRayDirection</c> in <c>sdf-world.hlsli</c> casts, left unnormalized. The camera's floats enter
    /// fixed point once, through <see cref="FixedVector3.FromVector3"/> and <see cref="FixedQ4816.FromDouble"/>.</summary>
    /// <param name="camera">The camera; its aspect ratio is the image's width over its height.</param>
    /// <param name="image">The point on the image, <c>x</c> right and <c>y</c> down, each in <c>[0, 1]</c> across the
    /// image.</param>
    /// <returns>The ray from the camera's position.</returns>
    public static SourceRay Through(CameraSnapshot camera, FixedVector2 image) {
        var tangent = FixedQ4816.FromDouble(value: camera.TanHalfFieldOfView);
        var horizontal = ((((image.X + image.X) - FixedQ4816.One) * FixedQ4816.FromDouble(value: camera.AspectRatio)) * tangent);
        var vertical = ((FixedQ4816.One - (image.Y + image.Y)) * tangent);

        return new SourceRay(
            Direction: ((FixedVector3.FromVector3(value: camera.Forward) + (FixedVector3.FromVector3(value: camera.Right) * horizontal)) + (FixedVector3.FromVector3(value: camera.Up) * vertical)),
            Origin: FixedVector3.FromVector3(value: camera.Position)
        );
    }
}
