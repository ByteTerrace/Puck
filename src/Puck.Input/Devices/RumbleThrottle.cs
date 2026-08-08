using System.Diagnostics;

namespace Puck.Input.Devices;

internal struct RumbleThrottle {
    private const long WriteIntervalMilliseconds = 30L;

    private long m_lastSendTicks;
    private float m_lastIntensity;

    public bool ShouldSend(float intensity) {
        var now = Stopwatch.GetTimestamp();

        if ((0f < intensity) && (intensity <= m_lastIntensity) && (0L != m_lastSendTicks)) {
            var elapsedMilliseconds = (((now - m_lastSendTicks) * 1000L) / Stopwatch.Frequency);

            if (elapsedMilliseconds < WriteIntervalMilliseconds) {
                return false;
            }
        }

        m_lastIntensity = intensity;
        m_lastSendTicks = now;

        return true;
    }
}
