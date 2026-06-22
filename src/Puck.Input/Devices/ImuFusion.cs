using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>
/// A complementary filter that fuses a controller's gyroscope and accelerometer into an absolute orientation.
/// The gyro supplies smooth short-term rotation (integrated each step) and the accelerometer, which reads the
/// gravity vector at rest, corrects the slow drift that integration accumulates. Without a magnetometer the yaw
/// is unreferenced (it tracks turns but drifts over time); pitch and roll stay anchored to gravity.
/// </summary>
internal static class ImuFusion {
    // How hard each step pulls the estimated up toward the measured (accelerometer) up. Small enough to ignore
    // transient linear acceleration, large enough to cancel gyro drift within a fraction of a second.
    private const float AccelerometerTrust = 0.02f;
    private const float Epsilon = 1e-6f;

    private static readonly Vector3 WorldUp = new(x: 0f, y: 1f, z: 0f);

    /// <summary>Advances an orientation estimate by one IMU sample.</summary>
    /// <param name="orientation">The current body-to-world orientation (start from <see cref="Quaternion.Identity"/>).</param>
    /// <param name="gyroRadiansPerSecond">The body-frame angular velocity, in radians per second.</param>
    /// <param name="accelerometerG">The accelerometer reading, in g (its direction is the measured up at rest).</param>
    /// <param name="deltaSeconds">The elapsed time since the previous sample, in seconds.</param>
    /// <returns>The updated, normalized orientation.</returns>
    public static Quaternion Integrate(Quaternion orientation, Vector3 gyroRadiansPerSecond, Vector3 accelerometerG, float deltaSeconds) {
        // 1. Integrate the body-frame angular velocity into the orientation (post-multiply applies it in body space).
        var speed = gyroRadiansPerSecond.Length();

        if ((speed > Epsilon) && (deltaSeconds > 0f)) {
            var delta = Quaternion.CreateFromAxisAngle(axis: (gyroRadiansPerSecond / speed), angle: (speed * deltaSeconds));

            orientation = Quaternion.Normalize(value: (orientation * delta));
        }

        // 2. Correct drift: rotate the estimate so its predicted up eases toward the measured (gravity) up.
        var magnitude = accelerometerG.Length();

        if (magnitude > 1e-3f) {
            var measuredUp = (accelerometerG / magnitude);
            var predictedUp = Vector3.Transform(value: WorldUp, rotation: Quaternion.Conjugate(value: orientation));
            // Mahony error term: cross(measured, estimated). This ordering makes the aligned pose the stable
            // point; reversing the operands would make the 180° antiparallel pose stable instead.
            var axis = Vector3.Cross(vector1: measuredUp, vector2: predictedUp);
            var axisLength = axis.Length();

            if (axisLength > Epsilon) {
                var angle = (AccelerometerTrust * MathF.Acos(x: Math.Clamp(value: Vector3.Dot(vector1: predictedUp, vector2: measuredUp), max: 1f, min: -1f)));
                var correction = Quaternion.CreateFromAxisAngle(axis: (axis / axisLength), angle: angle);

                orientation = Quaternion.Normalize(value: (orientation * correction));
            }
        }

        return orientation;
    }
}
