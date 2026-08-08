using Puck.Maths;

namespace Puck.Demo.Garden;

/// <summary>
/// A baked spherical direction grid (yaw × pitch), exactly the compile-time-rounded-once raw-Q16-constant pattern
/// <see cref="Overworld.OverworldWorld"/>'s movement direction sets (<c>FourDirections</c>/<c>EightDirections</c>/
/// <c>HexDirections</c>) already use — every entry is <c>sin</c>/<c>cos</c> rounded ONCE, here, to the nearest raw
/// Q48.16 tick, so a branch's direction is always a TABLE LOOKUP (integer index arithmetic) rather than a runtime
/// transcendental call: the tree's STRUCTURE (which way each branch points) stays fixed-point/integer end to end, as
/// the deterministic-garden design calls for. <see cref="Direction"/> combines two table entries with
/// <see cref="FixedQ4816"/> multiplies (still integer-only — the <c>*</c> operator's <c>Int128</c> intermediate) to
/// build a world-space unit(-ish) vector; the small baked-rounding error (well under a tenth of a percent) is
/// invisible at branch scale.
/// <para>
/// Yaw sweeps the full circle around world +Y (azimuth); pitch measures the lean AWAY from straight up (0 = vertical
/// trunk, the last entry past horizontal — a drooping/weeping lean for a seed that draws it). Both counts are small
/// on purpose: a tree only ever needs a handful of distinguishable directions, not smooth continuous rotation.
/// </para>
/// </summary>
internal static class GardenDirectionTable {
    /// <summary>The number of yaw steps around the full circle (22.5° each).</summary>
    internal const int YawSteps = 16;
    /// <summary>The number of pitch levels from vertical (index 0) to a past-horizontal droop (the last index).</summary>
    internal const int PitchLevels = 7;
    /// <summary>The last legal pitch index — callers clamp a computed pitch into <c>[0, MaxPitchIndex]</c>.</summary>
    internal const int MaxPitchIndex = (PitchLevels - 1);

    // sin/cos of i * 22.5 degrees, baked as raw Q48.16 ticks (value * 65536, rounded once, here).
    private static readonly FixedQ4816[] YawSin = [
        FixedQ4816.FromRawBits(value: 0L), FixedQ4816.FromRawBits(value: 25080L), FixedQ4816.FromRawBits(value: 46341L), FixedQ4816.FromRawBits(value: 60547L),
        FixedQ4816.FromRawBits(value: 65536L), FixedQ4816.FromRawBits(value: 60547L), FixedQ4816.FromRawBits(value: 46341L), FixedQ4816.FromRawBits(value: 25080L),
        FixedQ4816.FromRawBits(value: 0L), FixedQ4816.FromRawBits(value: -25080L), FixedQ4816.FromRawBits(value: -46341L), FixedQ4816.FromRawBits(value: -60547L),
        FixedQ4816.FromRawBits(value: -65536L), FixedQ4816.FromRawBits(value: -60547L), FixedQ4816.FromRawBits(value: -46341L), FixedQ4816.FromRawBits(value: -25080L),
    ];
    private static readonly FixedQ4816[] YawCos = [
        FixedQ4816.FromRawBits(value: 65536L), FixedQ4816.FromRawBits(value: 60547L), FixedQ4816.FromRawBits(value: 46341L), FixedQ4816.FromRawBits(value: 25080L),
        FixedQ4816.FromRawBits(value: 0L), FixedQ4816.FromRawBits(value: -25080L), FixedQ4816.FromRawBits(value: -46341L), FixedQ4816.FromRawBits(value: -60547L),
        FixedQ4816.FromRawBits(value: -65536L), FixedQ4816.FromRawBits(value: -60547L), FixedQ4816.FromRawBits(value: -46341L), FixedQ4816.FromRawBits(value: -25080L),
        FixedQ4816.FromRawBits(value: 0L), FixedQ4816.FromRawBits(value: 25080L), FixedQ4816.FromRawBits(value: 46341L), FixedQ4816.FromRawBits(value: 60547L),
    ];

    // sin/cos of pitch-from-vertical angles {0, 10, 22, 36, 52, 70, 92} degrees, baked the same way.
    private static readonly FixedQ4816[] PitchSin = [
        FixedQ4816.FromRawBits(value: 0L), FixedQ4816.FromRawBits(value: 11380L), FixedQ4816.FromRawBits(value: 24550L), FixedQ4816.FromRawBits(value: 38521L),
        FixedQ4816.FromRawBits(value: 51643L), FixedQ4816.FromRawBits(value: 61584L), FixedQ4816.FromRawBits(value: 65496L),
    ];
    private static readonly FixedQ4816[] PitchCos = [
        FixedQ4816.FromRawBits(value: 65536L), FixedQ4816.FromRawBits(value: 64540L), FixedQ4816.FromRawBits(value: 60764L), FixedQ4816.FromRawBits(value: 53020L),
        FixedQ4816.FromRawBits(value: 40348L), FixedQ4816.FromRawBits(value: 22415L), FixedQ4816.FromRawBits(value: -2287L),
    ];

    /// <summary>Looks up the unit(-ish) world-space direction for a (pitch, yaw) grid cell.</summary>
    /// <param name="pitchIndex">The pitch level, clamped into <c>[0, MaxPitchIndex]</c>.</param>
    /// <param name="yawIndex">The yaw step, taken modulo <see cref="YawSteps"/> (negative-safe).</param>
    /// <returns>A world-space direction vector (Y-up spherical: yaw is azimuth around +Y, pitch leans away from it).</returns>
    internal static FixedVector3 Direction(int pitchIndex, int yawIndex) {
        var p = Math.Clamp(value: pitchIndex, min: 0, max: MaxPitchIndex);
        var y = (((yawIndex % YawSteps) + YawSteps) % YawSteps);
        var sinP = PitchSin[p];
        var cosP = PitchCos[p];

        return new FixedVector3(
            X: (sinP * YawSin[y]),
            Y: cosP,
            Z: (sinP * YawCos[y])
        );
    }
}
