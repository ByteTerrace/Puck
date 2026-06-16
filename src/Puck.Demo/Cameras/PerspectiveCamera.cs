using System.Numerics;

namespace Puck.Demo.Cameras;

/// <summary>A look-at perspective camera: it derives its basis from a position and a target each
/// capture, so callers steer it by moving either endpoint. The demo drives this one as the free camera.</summary>
internal sealed class PerspectiveCamera : ICamera {
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
