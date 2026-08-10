using Puck.Maths.Research;

namespace Puck.Maths;

/// <summary>
/// Computes the eventually periodic continued-fraction expansion of an exact quadratic irrational, in pure integer
/// arithmetic.
/// </summary>
/// <remarks>
/// The input is the quadratic irrational <c>(p + q·√d) / r</c>. The expansion is produced by the classical surd recurrence on a
/// canonical <c>(P + √N) / Q</c> form (with <c>N = q²·d</c>), normalized so that <c>Q</c> divides <c>N − P²</c> and every
/// subsequent step divides exactly; the state <c>(P, Q)</c> is finite, so a repeated state marks the start of the period. By
/// Lagrange's theorem the expansion is eventually periodic; the golden ratio <c>(1 + √5) / 2</c> codes to the all-ones
/// period <c>[1; 1, 1, …]</c> and the silver ratio <c>1 + √2</c> to the all-twos period <c>[2; 2, 2, …]</c>, the same two
/// units that drive the golden and silver cases of <see cref="MetallicQuasicrystal"/>. All coefficients are exact
/// integers — there is no approximate seam here at all.
/// </remarks>
public static class ContinuedFraction {
    /// <summary>Expands the quadratic irrational <c>(p + q·√d) / r</c> into its eventually periodic continued fraction.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <param name="terms">Receives the partial quotients: the pre-period followed by exactly one period block. It must be long enough to hold them.</param>
    /// <param name="periodStart">Receives the index in <paramref name="terms"/> where the repeating block begins.</param>
    /// <param name="periodLength">Receives the length of the repeating block.</param>
    /// <returns>The number of partial quotients written to <paramref name="terms"/> — <paramref name="periodStart"/> plus <paramref name="periodLength"/>. The block <c>terms[periodStart..]</c> repeats forever.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> is not positive, <paramref name="d"/> is below two or a perfect square, or <paramref name="r"/> is zero.</exception>
    /// <exception cref="ArgumentException"><paramref name="terms"/> is too short to hold the pre-period and one period block.</exception>
    /// <exception cref="OverflowException">A partial quotient is outside the signed 64-bit range.</exception>
    /// <remarks>The recurrence is <see cref="QuadraticSurdRecurrence"/>, whose remarks carry the exactness contract these 64-bit parameters rest on: <c>q²·d</c> and the normalization by <c>r²</c> reach 315 bits, so the expansion itself runs in <see cref="System.Numerics.BigInteger"/>.</remarks>
    public static int Expand(long p, long q, long d, long r, Span<long> terms, out int periodStart, out int periodLength) {
        if (0L >= q) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(q),
                message: "the surd coefficient must be positive"
            );
        }

        if (2L > d) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(d),
                message: "the radicand must be at least two"
            );
        }

        if (0L == r) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(r),
                message: "the denominator must be non-zero"
            );
        }

        var radicandRoot = (long)((ulong)d).SquareRoot();

        if ((radicandRoot * radicandRoot) == d) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(d),
                message: "the radicand must not be a perfect square (the value would be rational)"
            );
        }

        var expansion = new QuadraticSurdExpansion(
            rationalNumerator: p,
            surdCoefficient: q,
            radicand: d,
            denominator: r
        );

        while (expansion.MoveNext()) {
            if (expansion.Index >= terms.Length) {
                throw new ArgumentException(
                    message: "terms is too short to hold the pre-period and one period block",
                    paramName: nameof(terms)
                );
            }

            terms[expansion.Index] = checked((long)expansion.Quotient);
        }

        periodStart = expansion.PeriodStart;
        periodLength = expansion.PeriodLength;

        return expansion.Count;
    }
}
