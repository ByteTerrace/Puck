using System.Diagnostics;
using Puck.Maths;

namespace Puck.Abstractions.Waiting;

/// <summary>The instant a wait through <see cref="HostWaits"/> gives up, as a <see cref="Stopwatch"/> timestamp. There is
/// no infinite deadline: <c>default</c> is the start of the clock, so a deadline nobody set has already passed.
/// <para>A deadline is a slice or a stall. A slice (<see cref="In"/>, <see cref="At"/>) bounds a wait its caller can
/// resume later, so on the host loop's pump thread <see cref="HostWaits"/> clips it to what the iteration's allowance has
/// left. A stall (<see cref="Stall"/>, <see cref="Standalone"/>) bounds a wait its caller cannot resume: a synchronous
/// door that completes its work or fails once the stall passes. Clipping a stall to the allowance could only fail it
/// early, so <see cref="HostWaits"/> never clips one and only charges the time it blocked.</para></summary>
public readonly record struct WaitDeadline {
    /// <summary>The stall every synchronous door that completes or fails shares: thirty seconds.</summary>
    public static readonly TimeSpan StandaloneStall = TimeSpan.FromSeconds(seconds: 30);

    private WaitDeadline(long timestamp, bool isStall) {
        Timestamp = timestamp;
        IsStall = isStall;
    }

    /// <summary>Gets the deadline as a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public long Timestamp { get; }
    /// <summary>Gets whether the deadline bounds a wait its caller cannot resume, which the pump allowance never
    /// clips.</summary>
    public bool IsStall { get; }
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
    public static WaitDeadline At(long timestamp) => new(isStall: false, timestamp: timestamp);
    /// <summary>Returns the slice deadline <paramref name="budget"/> from now.</summary>
    /// <param name="budget">How long the wait may take; zero or less has already passed.</param>
    /// <returns>The deadline.</returns>
    public static WaitDeadline In(TimeSpan budget) => new(isStall: false, timestamp: Stopwatch.GetTimestamp().AddSaturating(right: TicksOf(budget: budget)));
    /// <summary>Returns the stall deadline <paramref name="budget"/> from now: the bound of a wait its caller cannot
    /// resume.</summary>
    /// <param name="budget">How long the wait may take; zero or less has already passed.</param>
    /// <returns>The deadline.</returns>
    public static WaitDeadline Stall(TimeSpan budget) => new(isStall: true, timestamp: Stopwatch.GetTimestamp().AddSaturating(right: TicksOf(budget: budget)));
    /// <summary>Returns the stall deadline <see cref="StandaloneStall"/> from now: the bound of a synchronous door over a
    /// resumable operation, which completes the operation or throws.</summary>
    /// <returns>The deadline.</returns>
    public static WaitDeadline Standalone() => Stall(budget: StandaloneStall);

    /// <summary>Returns the earlier of this deadline and <paramref name="other"/>, a stall only when both are: a wait under
    /// both is resumable when either caller can resume it.</summary>
    /// <param name="other">The other deadline.</param>
    /// <returns>The combined deadline.</returns>
    public WaitDeadline Earliest(WaitDeadline other) => new(isStall: (IsStall && other.IsStall), timestamp: Math.Min(val1: Timestamp, val2: other.Timestamp));

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
