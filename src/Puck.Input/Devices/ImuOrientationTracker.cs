using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>
/// Per-device orientation state for the complementary <see cref="ImuFusion"/> filter: it learns and removes the
/// gyro's zero-rate bias while the device is held still and accumulates the fused pose. The integration step is
/// timed by a <paramref name="deltaSeconds"/> the caller supplies — derived from the device's own report clock
/// (the DualSense sensor timestamp) or its fixed sub-sample cadence (the Switch), <em>not</em> a wall clock — so
/// the fusion holds no hidden time source and the same report stream fuses identically. Callers feed gyro (rad/s)
/// and accelerometer (g) already expressed in a right-handed frame (X = right, Y = up, Z = back); each device
/// parser maps its own sensor axes into that frame.
/// </summary>
internal sealed class ImuOrientationTracker {
    // Below this rotation rate (with steady ~1g) the device is treated as still, so the gyro bias is learned and
    // subtracted — otherwise the unreferenced yaw would drift on that bias. dt is clamped to a sane report cadence.
    private const float GyroStationaryThreshold = 0.10f; // rad/s
    private const float GyroBiasLearnRate = 0.02f;
    private const float MinIntegrationSeconds = 0.0002f;
    private const float MaxIntegrationSeconds = 0.02f;

    private Vector3 m_gyroBias;
    private bool m_hasSample;
    private Quaternion m_orientation = Quaternion.Identity;

    /// <summary>The current fused orientation.</summary>
    public Quaternion Orientation => m_orientation;

    /// <summary>Advances the orientation by one IMU sample (gyro in rad/s, accel in g, both right-handed).</summary>
    /// <param name="gyroRadiansPerSecond">Body-frame angular velocity, right-handed.</param>
    /// <param name="accelerometerG">Accelerometer reading, right-handed.</param>
    /// <param name="deltaSeconds">
    /// Elapsed time since the previous sample, from the device's own report clock. Clamped to a sane report
    /// cadence, so a stale or first-sample value degrades to a bounded step rather than a spike. A non-positive
    /// value only learns bias (no integration), which is what the first sample of a stream wants.
    /// </param>
    /// <returns>The updated orientation.</returns>
    public Quaternion Update(Vector3 gyroRadiansPerSecond, Vector3 accelerometerG, float deltaSeconds) {
        // Learn the gyro's zero-rate bias while the device is held still, then remove it so yaw doesn't drift.
        if ((gyroRadiansPerSecond.Length() < GyroStationaryThreshold) && (MathF.Abs(x: (accelerometerG.Length() - 1f)) < 0.1f)) {
            m_gyroBias = Vector3.Lerp(value1: m_gyroBias, value2: gyroRadiansPerSecond, amount: GyroBiasLearnRate);
        }

        // Integrate only once a prior sample has established the stream and a positive step is available; the very
        // first sample (or a missing timestamp) just seeds the bias estimate and the orientation stays identity.
        if (m_hasSample && (deltaSeconds > 0f)) {
            var clamped = Math.Clamp(value: deltaSeconds, max: MaxIntegrationSeconds, min: MinIntegrationSeconds);

            m_orientation = ImuFusion.Integrate(orientation: m_orientation, gyroRadiansPerSecond: (gyroRadiansPerSecond - m_gyroBias), accelerometerG: accelerometerG, deltaSeconds: clamped);
        } else {
            m_hasSample = true;
        }

        return m_orientation;
    }
}
