using System.Numerics;

namespace Puck.Cameras;

public sealed class OrbitCamera : ICamera {
    public float AngularSpeedRadiansPerSecond { get; set; } = 0.45f;
    public float AzimuthRadians { get; set; }
    public float FieldOfViewRadians { get; set; } = (MathF.PI / 3f);
    public float Height { get; set; } = 1.8f;
    public float Radius { get; set; } = 4.5f;
    public Vector3 Target { get; set; } = Vector3.Zero;

    public void Advance(float deltaSeconds) {
        AzimuthRadians += (deltaSeconds * AngularSpeedRadiansPerSecond);
    }
    public CameraSnapshot Capture(uint viewportWidth, uint viewportHeight) {
        var position = (Target + new Vector3(
            x: (MathF.Cos(x: AzimuthRadians) * Radius),
            y: Height,
            z: (MathF.Sin(x: AzimuthRadians) * Radius)
        ));

        return CameraSnapshot.LookAt(
            fieldOfViewRadians: FieldOfViewRadians,
            position: position,
            target: Target,
            viewportHeight: viewportHeight,
            viewportWidth: viewportWidth
        );
    }
}
