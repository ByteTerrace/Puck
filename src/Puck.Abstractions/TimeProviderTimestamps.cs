namespace Puck.Abstractions;

/// <summary>Arithmetic between milliseconds and a <see cref="TimeProvider"/>'s own timestamp ticks, so a duration and a
/// deadline are spelled on the clock that will read them, never on <see cref="TimeSpan"/> ticks or the system
/// frequency.</summary>
public static class TimeProviderTimestamps {
    /// <summary>Converts a span of milliseconds to the provider's timestamp ticks, rounding toward zero.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="milliseconds">The span, in milliseconds; at most <c>long.MaxValue / time.TimestampFrequency</c>.</param>
    /// <returns>The span, in <paramref name="time"/>'s timestamp ticks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="OverflowException">The span in ticks exceeds <see cref="long.MaxValue"/>.</exception>
    public static long TimestampTicks(this TimeProvider time, long milliseconds) {
        ArgumentNullException.ThrowIfNull(argument: time);

        return checked((milliseconds * time.TimestampFrequency) / 1000L);
    }
    /// <summary>Returns the timestamp a span of milliseconds from now.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="milliseconds">The span, in milliseconds.</param>
    /// <returns>The provider's current timestamp plus the span, in timestamp ticks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="OverflowException">The span in ticks exceeds <see cref="long.MaxValue"/>.</exception>
    public static long TimestampAfter(this TimeProvider time, long milliseconds) =>
        (time.GetTimestamp() + time.TimestampTicks(milliseconds: milliseconds));
}
