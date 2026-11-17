using System.Diagnostics;
using Puck.Maths;

namespace Puck.Abstractions.Waiting;

/// <summary>The instant a wait through <see cref="HostWaits"/> gives up, as a <see cref="Stopwatch"/> timestamp. There is
/// no infinite deadline: <c>default</c> is the start of the clock, so a deadline nobody set has already passed.</summary>
public readonly record struct WaitDeadline {
    private WaitDeadline(long timestamp) {
        Timestamp = timestamp;
    }

    /// <summary>Gets the deadline as a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public long Timestamp { get; }
    /// <summary>Gets whether the deadline has passed.</summary>
    public bool HasPassed => (Stopwatch.GetTimestamp() >= Timestamp);
    /// <summary>Gets the time left before the deadline, or zero once it has passed.</summary>
    public TimeSpan Remaining {
        get {
            var remaining = (Timestamp - Stopwatch.GetTimestamp());

            return ((remaining <= 0L) ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startingTimestamp: 0L, endingTimestamp: remaining));
        }
    }

    /// <summary>Returns the deadline at a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    /// <param name="timestamp">The timestamp.</param>
    /// <returns>The deadline.</returns>
    public static WaitDeadline At(long timestamp) => new(timestamp: timestamp);
    /// <summary>Returns the deadline <paramref name="budget"/> from now.</summary>
    /// <param name="budget">How long the wait may take; zero or less has already passed.</param>
    /// <returns>The deadline.</returns>
    public static WaitDeadline In(TimeSpan budget) => new(timestamp: Stopwatch.GetTimestamp().AddSaturating(right: TicksOf(budget: budget)));

    /// <summary>Converts a duration to <see cref="Stopwatch"/> ticks, saturating instead of overflowing.</summary>
    /// <param name="budget">The duration; zero or less converts to zero.</param>
    /// <returns>The ticks.</returns>
    public static long TicksOf(TimeSpan budget) {
        if (budget <= TimeSpan.Zero) {
            return 0L;
        }

        var ticks = (budget.TotalSeconds * Stopwatch.Frequency);

        return ((ticks >= long.MaxValue) ? long.MaxValue : (long)ticks);
    }
}
