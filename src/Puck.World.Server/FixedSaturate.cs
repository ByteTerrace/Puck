using Puck.Maths;

namespace Puck.World.Server;

/// <summary>Narrowing helpers for the widened accumulators fixed-point reactions compute in.</summary>
/// <remarks>A reaction that sums or divides <see cref="FixedQ4816"/> raw bits widens to <see cref="Int128"/> so the
/// intermediate cannot wrap; the result still has to land in a 64-bit raw. Clamping to the extremes rather than
/// wrapping keeps an overflow deterministic and directionally honest.</remarks>
internal static class FixedSaturate {
    /// <summary>Clamps a widened accumulator into the <see cref="long"/> range and narrows it.</summary>
    /// <param name="value">The widened value.</param>
    /// <returns><paramref name="value"/> narrowed, or the nearer <see cref="long"/> extreme when it lies outside.</returns>
    internal static long ToInt64(Int128 value) => (
        (value <= long.MinValue)
            ? long.MinValue
            : ((value >= long.MaxValue)
                ? long.MaxValue
                : ((long)value))
    );
    /// <summary>Adds two fixed-point values, clamping to the representable extremes instead of wrapping.</summary>
    /// <param name="left">The left addend.</param>
    /// <param name="right">The right addend.</param>
    /// <returns>The saturated sum.</returns>
    internal static FixedQ4816 Add(FixedQ4816 left, FixedQ4816 right) => FixedQ4816.FromRawBits(
        value: ToInt64(value: (((Int128)left.Value) + right.Value))
    );
}
