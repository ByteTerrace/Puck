using System.Diagnostics;
using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>
/// Per-device orientation state for the complementary <see cref="ImuFusion"/> filter: it times the integration
/// step from the wall clock, learns and removes the gyro's zero-rate bias while the device is held still, and
/// accumulates the fused pose. Callers feed gyro (rad/s) and accelerometer (g) already expressed in a
/// right-handed frame (X = right, Y = up, Z = back); each device parser maps its own sensor axes into that frame.
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
    private long m_lastSampleTicks;
    private Quaternion m_orientation = Quaternion.Identity;

    /// <summary>The current fused orientation.</summary>
    public Quaternion Orientation => m_orientation;

    /// <summary>Advances the orientation by one IMU sample (gyro in rad/s, accel in g, both right-handed).</summary>
    /// <param name="gyroRadiansPerSecond">Body-frame angular velocity, right-handed.</param>
    /// <param name="accelerometerG">Accelerometer reading, right-handed.</param>
    /// <returns>The updated orientation.</returns>
    public Quaternion Update(Vector3 gyroRadiansPerSecond, Vector3 accelerometerG) {
        var now = Stopwatch.GetTimestamp();

        // Learn the gyro's zero-rate bias while the device is held still, then remove it so yaw doesn't drift.
        if ((gyroRadiansPerSecond.Length() < GyroStationaryThreshold) && (MathF.Abs(x: (accelerometerG.Length() - 1f)) < 0.1f)) {
            m_gyroBias = Vector3.Lerp(value1: m_gyroBias, value2: gyroRadiansPerSecond, amount: GyroBiasLearnRate);
        }

        if (m_hasSample) {
            var deltaSeconds = Math.Clamp(value: ((float)((now - m_lastSampleTicks) / (double)Stopwatch.Frequency)), max: MaxIntegrationSeconds, min: MinIntegrationSeconds);

            m_orientation = ImuFusion.Integrate(orientation: m_orientation, gyroRadiansPerSecond: (gyroRadiansPerSecond - m_gyroBias), accelerometerG: accelerometerG, deltaSeconds: deltaSeconds);
        } else {
            m_hasSample = true;
        }

        m_lastSampleTicks = now;

        return m_orientation;
    }
}
