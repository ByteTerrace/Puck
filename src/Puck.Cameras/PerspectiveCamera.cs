using System.Numerics;

namespace Puck.Cameras;

public sealed class PerspectiveCamera : ICamera {
    public float FieldOfViewRadians { get; set; } = (MathF.PI / 3f);
    public Vector3 Position { get; set; } = new(
        x: 0f,
        y: 1.5f,
        z: 4f
    );
    public Vector3 Target { get; set; } = Vector3.Zero;

    public CameraSnapshot Capture(uint viewportWidth, uint viewportHeight) {
        return CameraSnapshot.LookAt(
            fieldOfViewRadians: FieldOfViewRadians,
            position: Position,
            target: Target,
            viewportHeight: viewportHeight,
            viewportWidth: viewportWidth
        );
    }
}
