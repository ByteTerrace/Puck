namespace Puck.Maths.Tests;

/// <summary>Builds a <see cref="FixedVector3"/> from raw Q48.16 components, bypassing <see cref="FixedQ4816.FromInteger"/>'s
/// own scale: the carrier-extreme and rounding witnesses are raws, not integers that could be represented without
/// overflowing.</summary>
internal static class RawVectors {
    /// <summary>Builds a vector from three raw components.</summary>
    /// <param name="x">The first component's raw.</param>
    /// <param name="y">The second component's raw.</param>
    /// <param name="z">The third component's raw.</param>
    /// <returns>The vector.</returns>
    public static FixedVector3 Of(long x, long y, long z) =>
        new(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z)
        );
    /// <summary>Builds a vector from the first three raw components of a span.</summary>
    /// <param name="lanes">The raw components, at least three.</param>
    /// <returns>The vector.</returns>
    public static FixedVector3 Of(ReadOnlySpan<long> lanes) =>
        Of(x: lanes[0], y: lanes[1], z: lanes[2]);
}
