using System.Diagnostics;
using Puck.Maths;

namespace Puck.Abstractions.Waiting;

/// <summary>The instant a wait through <see cref="HostWaits"/> gives up, as a <see cref="Stopwatch"/> timestamp. There is
/// no infinite deadline: <c>default</c> is the start of the clock, so a deadline nobody set has already passed.
/// <para>A deadline is a slice or synchronous. A slice (<see cref="In"/>, <see cref="At"/>) bounds a wait its caller can
/// resume later, so on the host loop's pump thread <see cref="HostWaits"/> clips it to what the iteration's allowance has
/// left. A synchronous deadline (<see cref="Synchronous()"/>) bounds a wait its caller cannot resume: a door that
/// completes its work or fails once the deadline passes. A clip could only fail such a wait early, so
/// <see cref="HostWaits"/> never clips one and only charges the time it blocked.</para></summary>
public readonly record struct WaitDeadline {
    /// <summary>The budget of a synchronous door that completes a resumable operation or throws: thirty seconds.</summary>
    public static readonly TimeSpan SynchronousBudget = TimeSpan.FromSeconds(seconds: 30);

    private WaitDeadline(long timestamp, bool isSynchronous) {
        Timestamp = timestamp;
        IsSynchronous = isSynchronous;
    }

    /// <summary>Gets the deadline as a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public long Timestamp { get; }
    /// <summary>Gets whether the deadline bounds a wait its caller cannot resume, which the pump allowance never
    /// clips.</summary>
    public bool IsSynchronous { get; }
    /// <summary>Gets whether the deadline has passed.</summary>
    public bool HasPassed => (Stopwatch.GetTimestamp() >= Timestamp);
    /// <summary>Gets the time left before the deadline, or zero once it has passed.</summary>
    public TimeSpan Remaining {
        get {
            var remaining = (Timestamp - Stopwatch.GetTimestamp());

            return ((remaining <= 0L) ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startingTimestamp: 0L, endingTimestamp: remaining));
        }
    }

    /// <summary>Returns the slice deadline at a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    /// <param name="timestamp">The timestamp.</param>
    /// <returns>The deadline.</returns>
    public static WaitDeadline At(long timestamp) => new(isSynchronous: false, timestamp: timestamp);
    /// <summary>Returns the slice deadline <paramref name="budget"/> from now.</summary>
    /// <param name="budget">How long the wait may take; zero or less has already passed.</param>
    /// <returns>The deadline.</returns>
    public static WaitDeadline In(TimeSpan budget) => new(isSynchronous: false, timestamp: Stopwatch.GetTimestamp().AddSaturating(right: TicksOf(budget: budget)));
    /// <summary>Returns the synchronous deadline <paramref name="budget"/> from now: the bound of a wait its caller cannot
    /// resume.</summary>
    /// <param name="budget">How long the wait may take; zero or less has already passed.</param>
    /// <returns>The deadline.</returns>
    public static WaitDeadline Synchronous(TimeSpan budget) => new(isSynchronous: true, timestamp: Stopwatch.GetTimestamp().AddSaturating(right: TicksOf(budget: budget)));
    /// <summary>Returns the synchronous deadline <see cref="SynchronousBudget"/> from now: the bound of a door that
    /// completes a resumable operation or throws.</summary>
    /// <returns>The deadline.</returns>
    public static WaitDeadline Synchronous() => Synchronous(budget: SynchronousBudget);

    /// <summary>Returns the earlier of this deadline and <paramref name="other"/>, synchronous only when both are: a
    /// wait under both is resumable when either caller can resume it.</summary>
    /// <param name="other">The other deadline.</param>
    /// <returns>The combined deadline.</returns>
    public WaitDeadline Earliest(WaitDeadline other) => new(isSynchronous: (IsSynchronous && other.IsSynchronous), timestamp: Math.Min(val1: Timestamp, val2: other.Timestamp));

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
