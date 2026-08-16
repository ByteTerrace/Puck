using System.Diagnostics;

namespace Puck.Input.Devices;

internal struct RumbleThrottle {
    private const long WriteIntervalMilliseconds = 30L;

    private long m_lastSendTicks;
    private float m_lastIntensity;

    private static float Normalize(float intensity) =>
        (float.IsFinite(f: intensity)
            ? Math.Clamp(
                max: 1f,
                min: 0f,
                value: intensity
            )
            : 0f
        );
    private bool ShouldSend(float intensity) {
        var now = Stopwatch.GetTimestamp();

        if (
            (0f < intensity) &&
            (intensity <= m_lastIntensity) &&
            (0L != m_lastSendTicks)
        ) {
            var elapsedMilliseconds = (((now - m_lastSendTicks) * 1000L) / Stopwatch.Frequency);

            if (elapsedMilliseconds < WriteIntervalMilliseconds) {
                return false;
            }
        }

        m_lastIntensity = intensity;
        m_lastSendTicks = now;

        return true;
    }

    public void Reset() {
        this = default;
    }
    public bool TryPrepare(float lowFrequency, float highFrequency, out float low, out float high) {
        low = Normalize(intensity: lowFrequency);
        high = Normalize(intensity: highFrequency);

        return ShouldSend(intensity: MathF.Max(
            x: low,
            y: high
        ));
    }
}
