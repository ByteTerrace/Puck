namespace Puck.Abstractions.Gpu;

/// <summary>The sizes a growing GPU table takes: the one rule every table, region and scratch buffer grows by, with the
/// growth factor as a parameter. Units are the caller's (bytes, words, records).</summary>
public static class GpuCapacity {
    /// <summary>Returns the capacity a table holding <paramref name="current"/> grows to so it holds
    /// <paramref name="required"/>: <paramref name="current"/> when it already does; otherwise the largest of
    /// <paramref name="required"/>, <paramref name="minimum"/> and <paramref name="current"/> grown by one
    /// <paramref name="divisor"/>th of itself (at least one), the last capped at <paramref name="ceiling"/>. Growth by a
    /// fraction of the current size keeps the number of replacements logarithmic in the final size.</summary>
    /// <param name="current">The capacity held now; not negative.</param>
    /// <param name="required">The capacity needed; at most <paramref name="ceiling"/>.</param>
    /// <param name="ceiling">The largest capacity the table can take.</param>
    /// <param name="divisor">The growth step as a fraction of <paramref name="current"/>: 1 doubles, 2 grows by half; positive.</param>
    /// <param name="minimum">The smallest capacity a grown table takes; at most <paramref name="ceiling"/>.</param>
    /// <returns>The grown capacity, at least <paramref name="required"/> and at most <paramref name="ceiling"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="current"/> is negative, <paramref name="divisor"/>
    /// is not positive, or <paramref name="required"/> or <paramref name="minimum"/> is past
    /// <paramref name="ceiling"/>.</exception>
    public static long Grown(long current, long required, long ceiling, long divisor, long minimum = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(divisor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(required, ceiling);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimum, ceiling);
        if (required <= current) { return current; }
        var stepped = ((current >= (ceiling - Math.Max(val1: 1L, val2: (current / divisor))))
            ? ceiling
            : (current + Math.Max(val1: 1L, val2: (current / divisor))));

        return Math.Max(val1: Math.Max(val1: required, val2: minimum), val2: stepped);
    }
    /// <summary>Returns the size a table fitted to <paramref name="required"/> takes: <paramref name="required"/> plus one
    /// <paramref name="divisor"/>th of it as headroom, rounded up to a whole <paramref name="granule"/>, capped at
    /// <paramref name="ceiling"/>. A table fitted to its need, rather than grown from its current size, uses it when it
    /// grows and when it shrinks.</summary>
    /// <param name="required">The size needed; not negative and at most <paramref name="ceiling"/>.</param>
    /// <param name="ceiling">The largest size the table can take.</param>
    /// <param name="divisor">The headroom as a fraction of <paramref name="required"/>: 8 adds an eighth; positive.</param>
    /// <param name="granule">The unit sizes are rounded up to; positive.</param>
    /// <returns>The fitted size, at least <paramref name="required"/> and at most <paramref name="ceiling"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="required"/> is negative or past
    /// <paramref name="ceiling"/>, or <paramref name="divisor"/> or <paramref name="granule"/> is not positive.</exception>
    public static long Headroom(long required, long ceiling, long divisor, long granule = 1) {
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(required, ceiling);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(divisor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(granule);
        var wanted = checked((required + (required / divisor)));

        return Math.Min(val1: checked((((wanted + (granule - 1L)) / granule) * granule)), val2: ceiling);
    }
}
