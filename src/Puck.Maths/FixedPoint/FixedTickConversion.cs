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
}
