using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Deterministic low-discrepancy sequences (additive recurrences): index-to-point maps whose points cover the unit
/// interval or square more evenly than uniform random draws. Stateless (a pure function of the index); one multiply
/// per component, with the 64-bit wrap performing the mod-1 exactly.
/// </summary>
/// <remarks>
/// <see cref="R1"/> is the golden-ratio sequence; <see cref="R2"/> is the plastic-number generalization of the
/// golden-ratio sequence to two dimensions. Offsetting the index shifts the whole point set deterministically.
/// </remarks>
public static class LowDiscrepancy {
    // floor(2^64 / φ): the conventional odd Weyl increment, which gives the phase recurrence its full 2^64 period.
    private const ulong GoldenConjugateQ64 = SplitMix64.GoldenGamma;
    private const ulong PlasticInverseQ64 = 13925035116211876495UL;         // round(2^64 / ρ)
    private const ulong PlasticInverseSquaredQ64 = 10511698010929265437UL;  // round(2^64 / ρ²)

    /// <summary>The first R2 increment at 32 bits, <c>round(2^32 / ρ)</c>: <see cref="R2"/>'s first Q64 constant
    /// rounded to nearest, the alpha a 32-bit wrapping recurrence (a GPU dither) multiplies its index by.</summary>
    public const uint R2AlphaXQ32 = ((uint)((PlasticInverseQ64 + (1UL << 31)) >> 32));
    /// <summary>The second R2 increment at 32 bits, <c>round(2^32 / ρ²)</c>, rounded from <see cref="R2"/>'s second
    /// Q64 constant the same way.</summary>
    public const uint R2AlphaYQ32 = ((uint)((PlasticInverseSquaredQ64 + (1UL << 31)) >> 32));

    /// <summary>Returns the <paramref name="index"/>-th point of the one-dimensional golden-ratio sequence.</summary>
    /// <param name="index">The point index; consecutive indices land maximally far apart.</param>
    /// <returns>The fraction <c>frac(index / φ)</c> in <c>[0, 1)</c>.</returns>
    public static UnitFraction32 R1(ulong index) =>
        new(Value: ((uint)(unchecked((index * GoldenConjugateQ64)) >> 32)));
    /// <summary>Returns the <paramref name="index"/>-th point of the two-dimensional plastic-number sequence.</summary>
    /// <param name="index">The point index; consecutive indices cover the unit square evenly.</param>
    /// <returns>The pair <c>(frac(index / ρ), frac(index / ρ²))</c>, each in <c>[0, 1)</c>.</returns>
    public static (UnitFraction32 X, UnitFraction32 Y) R2(ulong index) =>
        (new(Value: ((uint)(unchecked((index * PlasticInverseQ64)) >> 32))),
         new(Value: ((uint)(unchecked((index * PlasticInverseSquaredQ64)) >> 32))));

    /// <summary>The golden angle in radians, <c>π · (3 − √5)</c>: the turn between consecutive points of a golden spiral
    /// (<see cref="SphericalFibonacci"/>, a disc's spiral taps), the angular form of <see cref="R1"/>'s increment.</summary>
    public const double GoldenAngle = 2.39996322972865332;

    /// <summary>Returns the <paramref name="index"/>-th of <paramref name="count"/> directions spread evenly over the unit
    /// sphere: the spherical Fibonacci lattice, a band of equal area per point along Y, each turned the golden angle from
    /// the last. Presentation tier (float).</summary>
    /// <param name="index">The direction's index, from zero to <paramref name="count"/>.</param>
    /// <param name="count">The directions in the set; positive.</param>
    /// <returns>The unit direction.</returns>
    public static Vector3 SphericalFibonacci(int index, int count) {
        var y = (1f - (((2f * index) + 1f) / count));
        var radius = MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (y * y))));
        var angle = (index * ((float)GoldenAngle));

        return new Vector3(x: (radius * MathF.Cos(x: angle)), y: y, z: (radius * MathF.Sin(x: angle)));
    }

    /// <summary>Returns the radical inverse of <paramref name="index"/> in base <paramref name="radix"/>: its digits
    /// mirrored about the radix point, the <paramref name="index"/>-th point of the van der Corput sequence, whose pairs in
    /// coprime bases are the Halton sequence. The digits accumulate exactly as an integer numerator over the radix power
    /// and divide once.</summary>
    /// <param name="index">The point's index.</param>
    /// <param name="radix">The base, at least two.</param>
    /// <returns>The point in <c>[0, 1)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radix"/> is below two.</exception>
    public static double RadicalInverse(uint index, uint radix) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: radix, other: 2u);

        var numerator = 0UL;
        var denominator = 1UL;

        while (index != 0u) {
            numerator = ((numerator * radix) + (index % radix));
            denominator *= radix;
            index /= radix;
        }
        return (numerator / ((double)denominator));
    }
}
