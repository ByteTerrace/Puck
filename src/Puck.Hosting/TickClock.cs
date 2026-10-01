namespace Puck.Hosting;

/// <summary>Measures the host time elapsed between samples of a <see cref="TimeProvider"/>, in
/// <see cref="EngineTicks"/>, carrying the conversion remainder so no time is lost between samples. A host loop reads its
/// wall clock only through this and the same provider's timestamps, so a law can hand it a clock of its own.</summary>
public struct TickClock {
    private readonly ulong m_frequency;
    private readonly TimeProvider m_time;

    private long m_previousTimestamp;
    private ulong m_remainder;

    private TickClock(TimeProvider time) {
        m_time = time;
        m_frequency = ((ulong)time.TimestampFrequency);
        m_previousTimestamp = time.GetTimestamp();
        m_remainder = 0UL;
    }

    /// <summary>Returns the engine ticks elapsed since the previous sample, or since the clock started.</summary>
    /// <returns>The elapsed time in engine ticks.</returns>
    public ulong Sample() {
        var now = m_time.GetTimestamp();
        var elapsed = unchecked((ulong)(now - m_previousTimestamp));

        m_previousTimestamp = now;

        var scaled = ((elapsed * EngineTicks.PerSecond) + m_remainder);

        (var quotient, m_remainder) = Math.DivRem(
            left: scaled,
            right: m_frequency
        );

        return quotient;
    }
    /// <summary>Starts a clock over <paramref name="time"/> whose first sample measures from now.</summary>
    /// <param name="time">The host's clock: <see cref="TimeProvider.System"/> outside a law.</param>
    /// <returns>The started clock.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    public static TickClock Start(TimeProvider time) {
        ArgumentNullException.ThrowIfNull(argument: time);

        return new TickClock(time: time);
    }
}
