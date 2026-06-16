using System.Diagnostics;

namespace Puck.Hosting;

public struct TickClock {
    private readonly ulong m_frequency;
    private long m_previousTimestamp;
    private ulong m_remainder;

    private TickClock(ulong frequency, long timestamp) {
        m_frequency = frequency;
        m_previousTimestamp = timestamp;
        m_remainder = 0UL;
    }

    public static TickClock Start() =>
        new(
            frequency: ((ulong)Stopwatch.Frequency),
            timestamp: Stopwatch.GetTimestamp()
        );
    public ulong Sample() {
        var now = Stopwatch.GetTimestamp();
        var elapsed = unchecked((ulong)(now - m_previousTimestamp));

        m_previousTimestamp = now;

        var scaled = ((elapsed * EngineTicks.PerSecond) + m_remainder);

        (var quotient, m_remainder) = Math.DivRem(
            left: scaled,
            right: m_frequency
        );

        return quotient;
    }
}
