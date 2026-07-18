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
    private const ulong GoldenConjugateQ64 = 0x9E3779B97F4A7C15UL;
    private const ulong PlasticInverseQ64 = 13925035116211876495UL;         // round(2^64 / ρ)
    private const ulong PlasticInverseSquaredQ64 = 10511698010929265437UL;  // round(2^64 / ρ²)

    /// <summary>Returns the <paramref name="index"/>-th point of the one-dimensional golden-ratio sequence.</summary>
    /// <param name="index">The point index; consecutive indices land maximally far apart.</param>
    /// <returns>The fraction <c>frac(index / φ)</c> in <c>[0, 1)</c>.</returns>
    public static UFixedQ0032 R1(ulong index) =>
        new(Value: ((uint)(unchecked((index * GoldenConjugateQ64)) >> 32)));
    /// <summary>Returns the <paramref name="index"/>-th point of the two-dimensional plastic-number sequence.</summary>
    /// <param name="index">The point index; consecutive indices cover the unit square evenly.</param>
    /// <returns>The pair <c>(frac(index / ρ), frac(index / ρ²))</c>, each in <c>[0, 1)</c>.</returns>
    public static (UFixedQ0032 X, UFixedQ0032 Y) R2(ulong index) =>
        (new(Value: ((uint)(unchecked((index * PlasticInverseQ64)) >> 32))),
         new(Value: ((uint)(unchecked((index * PlasticInverseSquaredQ64)) >> 32))));
}
