namespace Puck.Maths;

/// <summary>
/// The continued-fraction expansion of an exact quadratic irrational, computed in pure integer arithmetic. By Lagrange's
/// theorem the expansion of every quadratic irrational is eventually periodic, and that period is the symbolic coding of a
/// closed geodesic on the modular surface: the golden ratio <c>(1 + √5) / 2</c> codes to the all-ones period <c>[1; 1, 1, …]</c>
/// and the silver ratio <c>1 + √2</c> to the all-twos period <c>[2; 2, 2, …]</c> — the two shortest closed geodesics, the same
/// two units that drive the golden and silver cases of <see cref="MetallicQuasicrystal"/>.
/// </summary>
/// <remarks>
/// The input is the quadratic irrational <c>(p + q·√d) / r</c>. The expansion is produced by the classical surd recurrence on a
/// canonical <c>(P + √N) / Q</c> form (with <c>N = q²·d</c>), normalized so that <c>Q</c> divides <c>N − P²</c> and every
/// subsequent step divides exactly; the state <c>(P, Q)</c> is finite, so a repeated state marks the start of the period. All
/// coefficients are exact integers — there is no approximate seam here at all.
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

        // Canonical form (P + √N) / Q with N = q²·d, then scale so that Q divides N − P² and every later step divides exactly.
        var stateP = ((Int128)p);
        var stateN = (((Int128)q * q) * d);
        var stateQ = ((Int128)r);

        if (Int128.Zero != ((stateN - (stateP * stateP)) % stateQ)) {
            var magnitude = Int128.Abs(value: stateQ);

            stateP *= magnitude;
            stateN *= (stateQ * stateQ);
            stateQ *= magnitude;
        }

        var root = ((Int128)((UInt128)stateN).SquareRoot());
        var seenP = new List<Int128>();
        var seenQ = new List<Int128>();

        while (true) {
            var repeatAt = IndexOfState(
                seenP: seenP,
                seenQ: seenQ,
                stateP: stateP,
                stateQ: stateQ
            );

            if (0 <= repeatAt) {
                periodStart = repeatAt;
                periodLength = (seenP.Count - repeatAt);

                return seenP.Count;
            }

            var count = seenP.Count;

            if (count >= terms.Length) {
                throw new ArgumentException(
                    message: "terms is too short to hold the pre-period and one period block",
                    paramName: nameof(terms)
                );
            }

            // Floor of (P + √N) / Q: for a positive denominator the numerator floors with ⌊√N⌋; for a negative one the surd
            // sits just below ⌊√N⌋ + 1, which is the bound that floors correctly once the sign flips the inequality.
            var quotient = ((0 < stateQ)
                ? FloorDivide(numerator: (stateP + root), denominator: stateQ)
                : FloorDivide(numerator: ((stateP + root) + Int128.One), denominator: stateQ));

            seenP.Add(item: stateP);
            seenQ.Add(item: stateQ);
            terms[count] = ((long)quotient);

            var nextP = ((quotient * stateQ) - stateP);

            stateQ = ((stateN - (nextP * nextP)) / stateQ);
            stateP = nextP;
        }
    }

    private static int IndexOfState(List<Int128> seenP, List<Int128> seenQ, Int128 stateP, Int128 stateQ) {
        for (var index = 0; (index < seenP.Count); ++index) {
            if ((seenP[index] == stateP) && (seenQ[index] == stateQ)) {
                return index;
            }
        }

        return -1;
    }
    private static Int128 FloorDivide(Int128 numerator, Int128 denominator) {
        var quotient = (numerator / denominator);
        var remainder = (numerator - (quotient * denominator));

        return (quotient - ((remainder != Int128.Zero) && (Int128.IsNegative(value: remainder) != Int128.IsNegative(value: denominator))).As<Int128>());
    }
}
