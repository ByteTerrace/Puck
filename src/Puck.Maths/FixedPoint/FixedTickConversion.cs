namespace Puck.Maths;

/// <summary>
/// Converts a fixed-point duration in seconds to a whole count of engine ticks, rounding UP so a positive duration
/// is never rounded away to zero ticks. Single-sourced here (rather than beside either caller) because both
/// <c>Puck.World.Server.WorldBody</c> (the per-tick effect-duration consumer) and the world-document's kit-effect
/// compile path (<c>Puck.World.WorldKit</c>'s <c>ActionEffect</c> compilation, in <c>Puck.World.Data</c>) need the
/// IDENTICAL rounding rule, and Puck.World.Data must not reference Puck.World.Server — the two projects calling the
/// same Puck.Maths member is what dissolves that cycle, rather than one project reaching into the other for it.
/// </summary>
public static class FixedTickConversion {
    /// <summary>The number of engine ticks in one second (<c>50400 = 2⁵·3²·5²·7</c>), matching
    /// <c>Puck.Hosting.EngineTicks.PerSecond</c> exactly. Puck.Maths sits on the Leaf contracts and data row and
    /// cannot reference Puck.Hosting (a Shared-substrate project would be an upward dependency), so this pinned
    /// duplicate is the seam — both constants must be changed together, and neither has changed since either was
    /// introduced. The count is divisible by every common animation/display rate, including the 240 Hz rate
    /// <c>Puck.World</c>'s fixed simulation step runs at.</summary>
    public const ulong TicksPerSecond = 50400UL;

    /// <summary>Converts a duration to the smallest whole tick count no less than its exact value, so a positive
    /// duration always advances by at least one tick. Non-positive durations convert to zero.</summary>
    /// <param name="seconds">The duration to convert.</param>
    /// <returns><paramref name="seconds"/> in engine ticks, rounded up; <c>0</c> when <paramref name="seconds"/> is
    /// zero or negative.</returns>
    public static ulong DurationEngineTicks(FixedQ4816 seconds) {
        if (seconds <= FixedQ4816.Zero) {
            return 0UL;
        }

        var scaled = ((Int128)seconds.Value * TicksPerSecond);

        return checked((ulong)scaled.CeilingDivide(divisor: (Int128)(1L << FixedQ4816.FractionBitCount)));
    }

    /// <summary>Converts a duration to an EXACT whole engine-tick count — the non-rounding sibling of
    /// <see cref="DurationEngineTicks(FixedQ4816)"/>, for a caller that must REFUSE an inexact duration rather than
    /// silently round it. <paramref name="seconds"/> is <see cref="decimal"/> — base-10, exact for any terminating
    /// decimal literal — rather than a binary float: <see cref="FixedQ4816"/> and <see langword="float"/>/
    /// <see langword="double"/> all carry a binary (power-of-two) fraction, which cannot represent most terminating
    /// decimals exactly either (0.1 has no exact binary spelling any more than it has an exact <see cref="TicksPerSecond"/>-tick
    /// spelling), so routing this exactness check through either would reject values a decimal author reasonably
    /// expects to be exact, or accept values that are not. The arithmetic decomposes the decimal's 96-bit integer
    /// mantissa and base-10 scale into <see cref="UInt128"/> values, so neither decimal multiplication nor a
    /// binary-float intermediate can round or overflow.</summary>
    /// <param name="seconds">The duration, parsed directly from decimal text (a JSON number deserializes to
    /// <see cref="decimal"/> without a binary-float intermediate).</param>
    /// <param name="ticks">The exact whole engine-tick count, when this method returns <see langword="true"/>;
    /// <c>0</c> otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="seconds"/> is non-negative and a whole multiple of
    /// <c>1/<see cref="TicksPerSecond"/></c> second (equivalently, a multiple of 1/800 s — the largest
    /// terminating-decimal-compatible divisor of 50400, since only its factors of 2 and 5 can terminate in base 10);
    /// <see langword="false"/> for a negative duration, one that divides 50400 unevenly, or an exact duration whose
    /// tick count does not fit in <see cref="ulong"/>.</returns>
    public static bool TryDurationEngineTicksExact(decimal seconds, out ulong ticks) {
        if (seconds < 0m) {
            ticks = 0UL;

            return false;
        }

        var bits = decimal.GetBits(d: seconds);
        var scale = (int)(((uint)bits[3] >> 16) & 0xFFU);
        var unscaled = (((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0]);
        var denominator = UInt128.One;

        for (var index = 0; (index < scale); index++) {
            denominator *= 10U;
        }

        // A decimal mantissa is 96 bits and TicksPerSecond is 16 bits, so this product always fits UInt128.
        var numerator = (unscaled * TicksPerSecond);
        var wholeTicks = (numerator / denominator);

        if (((numerator % denominator) != UInt128.Zero) || (wholeTicks > ulong.MaxValue)) {
            ticks = 0UL;

            return false;
        }

        ticks = (ulong)wholeTicks;

        return true;
    }
}
