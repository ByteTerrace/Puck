using System.Numerics;

namespace Puck.Cameras;

public readonly record struct CameraSnapshot(
    Vector3 Position,
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    float TanHalfFieldOfView,
    float AspectRatio
) {
    public static CameraSnapshot LookAt(Vector3 position, Vector3 target, float fieldOfViewRadians, uint viewportWidth, uint viewportHeight) {
        var forward = SafeNormalize(
            fallback: -Vector3.UnitZ,
            value: (target - position)
        );
        var right = SafeNormalize(
            fallback: Vector3.UnitX,
            value: Vector3.Cross(
                vector1: forward,
                vector2: Vector3.UnitY
            )
        );
        var up = Vector3.Cross(
            vector1: right,
            vector2: forward
        );
        var aspectRatio = ((viewportHeight == 0)
            ? 1f
            : (viewportWidth / (float)viewportHeight));

        return new CameraSnapshot(
            AspectRatio: aspectRatio,
            Forward: forward,
            Position: position,
            Right: right,
            TanHalfFieldOfView: MathF.Tan(x: (fieldOfViewRadians * 0.5f)),
            Up: up
        );
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) {
        var length = value.Length();

        return ((length > 1e-5f)
            ? (value / length)
            : fallback);
    }
}
