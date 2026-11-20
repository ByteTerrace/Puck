using System.Numerics;

namespace Puck.Maths;

/// <summary>The capped doubling schedule of a retry or a deferred rebuild: each wait is twice the one before, from a first
/// wait up to a ceiling. The wait is an integer of the caller's unit (milliseconds, engine ticks, frames), so the
/// schedule is the same on every machine and never reads a clock; it needs no jitter where one owner retries a
/// resource.</summary>
public static class ExponentialBackoff {
    /// <summary>Returns the wait that follows <paramref name="current"/>.</summary>
    /// <typeparam name="T">The integer type of the wait.</typeparam>
    /// <param name="current">The wait just taken, or zero (or below) before the first.</param>
    /// <param name="first">The first wait.</param>
    /// <param name="ceiling">The longest wait, which the schedule never exceeds; at least <paramref name="first"/>.</param>
    /// <returns><paramref name="first"/> after a non-positive wait; otherwise twice <paramref name="current"/>, capped at
    /// <paramref name="ceiling"/>. The doubling cannot overflow.</returns>
    public static T Next<T>(T current, T first, T ceiling) where T : IBinaryInteger<T> {
        if (current <= T.Zero) { return T.Min(x: first, y: ceiling); }

        return ((current > (ceiling - current))
            ? ceiling
            : (current + current));
    }
    /// <summary>Returns the wait after a count of consecutive failures, by <see cref="Next{T}(T, T, T)"/> applied that many
    /// times from the first wait.</summary>
    /// <typeparam name="T">The integer type of the wait.</typeparam>
    /// <param name="failures">The failures so far; zero gives the first wait.</param>
    /// <param name="first">The first wait.</param>
    /// <param name="ceiling">The longest wait; at least <paramref name="first"/>.</param>
    /// <returns>The wait to take before the next attempt.</returns>
    public static T After<T>(int failures, T first, T ceiling) where T : IBinaryInteger<T> {
        var wait = T.Min(x: first, y: ceiling);

        for (var failure = 0; ((failure < failures) && (wait < ceiling)); failure++) {
            wait = Next(
                ceiling: ceiling,
                current: wait,
                first: first
            );
        }

        return wait;
    }
}
