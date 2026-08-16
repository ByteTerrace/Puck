using System.Numerics;
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
    /// <summary>Fills the leading convergents <c>p_k / q_k</c> of the quadratic irrational <c>(p + q·√d) / r</c>.</summary>
    /// <remarks>
    /// Convergents are the best rational approximations of the second kind: each strictly decreases
    /// <c>|q_k·x − p_k|</c>, and no fraction with a smaller denominator comes closer in that measure. They are
    /// therefore the exact worst-case indices for any consumer computing <c>⌊x·n⌋</c> through a rounded slope —
    /// the closest approaches of <c>x·n</c> to an integer — and they alternate sides, even indices below <c>x</c>
    /// and odd indices above. <see cref="BeattyQuantization"/> turns the same structure into divergence
    /// certificates.
    /// </remarks>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <param name="numerators">Receives <c>p_0, p_1, …</c>; the convergent count requested is its length.</param>
    /// <param name="denominators">Receives <c>q_0, q_1, …</c>; it must have the same length as <paramref name="numerators"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="numerators"/> and <paramref name="denominators"/> differ in length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> is not positive, <paramref name="d"/> is below two or a perfect square, or <paramref name="r"/> is zero.</exception>
    public static void Convergents(long p, long q, long d, long r, Span<BigInteger> numerators, Span<BigInteger> denominators) {
        if (numerators.Length != denominators.Length) {
            throw new ArgumentException(
                message: "numerators and denominators must have the same length",
                paramName: nameof(denominators)
            );
        }

        Span<long> terms = stackalloc long[128];
        int count;
        int periodStart;
        int periodLength;

        while (true) {
            try {
                count = Expand(
                    d: d,
                    p: p,
                    periodLength: out periodLength,
                    periodStart: out periodStart,
                    q: q,
                    r: r,
                    terms: terms
                );

                break;
            } catch (ArgumentException exception) when (((exception.ParamName == nameof(terms)) && (terms.Length < int.MaxValue))) {
                var nextLength = ((terms.Length <= (int.MaxValue / 2))
                    ? (terms.Length * 2)
                    : int.MaxValue
                );

                terms = new long[nextLength];
            }
        }

        var previousNumerator = BigInteger.One;
        var previousDenominator = BigInteger.Zero;
        var beforeNumerator = BigInteger.Zero;
        var beforeDenominator = BigInteger.One;

        for (var index = 0; (index < numerators.Length); ++index) {
            var term = terms[((index < count)
                ? index
                : (periodStart + ((index - periodStart) % periodLength)))];
            var currentNumerator = ((term * previousNumerator) + beforeNumerator);
            var currentDenominator = ((term * previousDenominator) + beforeDenominator);

            numerators[index] = currentNumerator;
            denominators[index] = currentDenominator;
            (beforeNumerator, beforeDenominator) = (previousNumerator, previousDenominator);
            (previousNumerator, previousDenominator) = (currentNumerator, currentDenominator);
        }
    }
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

        var radicandRoot = ((long)((ulong)d).SquareRoot());

        if ((radicandRoot * radicandRoot) == d) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(d),
                message: "the radicand must not be a perfect square (the value would be rational)"
            );
        }

        var expansion = new QuadraticSurdExpansion(
            denominator: r,
            radicand: d,
            rationalNumerator: p,
            surdCoefficient: q
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
