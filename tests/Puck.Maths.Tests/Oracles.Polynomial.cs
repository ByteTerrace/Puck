using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>An exact fraction held as a numerator over a positive denominator, unreduced: the oracle's own arithmetic,
    /// which calls no <c>Puck.Maths</c> kernel.</summary>
    /// <param name="Numerator">The numerator.</param>
    /// <param name="Denominator">The denominator, positive.</param>
    public readonly record struct ExactFraction(BigInteger Numerator, BigInteger Denominator) {
        public static ExactFraction Of(BigInteger value) => new(Numerator: value, Denominator: BigInteger.One);
        public static ExactFraction operator +(ExactFraction left, ExactFraction right) => new(
            Numerator: ((left.Numerator * right.Denominator) + (right.Numerator * left.Denominator)),
            Denominator: (left.Denominator * right.Denominator)
        );
        public static ExactFraction operator -(ExactFraction left, ExactFraction right) => (left + new ExactFraction(Numerator: -right.Numerator, Denominator: right.Denominator));
        public static ExactFraction operator *(ExactFraction left, ExactFraction right) => new(
            Numerator: (left.Numerator * right.Numerator),
            Denominator: (left.Denominator * right.Denominator)
        );
        public static ExactFraction operator /(ExactFraction left, BigInteger right) => ((right.Sign < 0)
            ? new(Numerator: -left.Numerator, Denominator: (left.Denominator * -right))
            : new(Numerator: left.Numerator, Denominator: (left.Denominator * right)));

        /// <summary>Tests the fraction against a numerator and denominator by cross multiplication.</summary>
        public bool Equals(BigInteger numerator, BigInteger denominator) => ((Numerator * denominator) == (numerator * Denominator));
        /// <summary>Returns the greatest integer no larger than the fraction.</summary>
        public BigInteger Floor() {
            var quotient = BigInteger.DivRem(dividend: Numerator, divisor: Denominator, remainder: out var remainder);

            return ((remainder.Sign < 0) ? (quotient - 1) : quotient);
        }
    }

    /// <summary>Evaluates a polynomial given as fraction coefficients, constant term first, as an explicit sum of
    /// coefficient times power, never by Horner's rule.</summary>
    public static ExactFraction PolynomialAt(IReadOnlyList<ExactFraction> coefficients, ExactFraction x) {
        var sum = ExactFraction.Of(value: BigInteger.Zero);
        var power = ExactFraction.Of(value: BigInteger.One);

        foreach (var coefficient in coefficients) {
            sum += (coefficient * power);
            power *= x;
        }

        return sum;
    }
    /// <summary>The integral of a polynomial between two points, from its monomials: each coefficient times the difference
    /// of the next power at the two ends over that power.</summary>
    public static ExactFraction PolynomialIntegral(IReadOnlyList<ExactFraction> coefficients, ExactFraction from, ExactFraction to) {
        var sum = ExactFraction.Of(value: BigInteger.Zero);
        var powerFrom = from;
        var powerTo = to;

        for (var k = 0; (k < coefficients.Count); k++) {
            sum += ((coefficients[k] * (powerTo - powerFrom)) / (k + 1));
            powerFrom *= from;
            powerTo *= to;
        }

        return sum;
    }
    /// <summary>The integral from zero to <paramref name="x"/> of a periodic piecewise polynomial, by walking every whole
    /// turn and every piece between them one at a time: the brute-force sum the closed form must equal.</summary>
    /// <param name="starts">Each piece's start within a turn, the first zero.</param>
    /// <param name="pieces">Each piece's coefficients in the phase.</param>
    /// <param name="x">The upper end, at any value.</param>
    public static ExactFraction PeriodicIntegral(IReadOnlyList<ExactFraction> starts, IReadOnlyList<IReadOnlyList<ExactFraction>> pieces, ExactFraction x) {
        var zero = ExactFraction.Of(value: BigInteger.Zero);
        var negative = (x.Numerator.Sign < 0);
        var lo = (negative ? x : zero);
        var hi = (negative ? zero : x);
        var total = zero;

        // Turn T spans [T, T + 1); its piece i spans [T + start(i), T + start(i + 1)), and the piece's polynomial is in
        // the phase u - T. Every turn the span [lo, hi] meets is walked, each piece clipped to the span.
        for (var turn = lo.Floor(); (turn <= hi.Floor()); turn++) {
            for (var index = 0; (index < starts.Count); index++) {
                var from = Max(left: (ExactFraction.Of(value: turn) + starts[index]), right: lo);
                var to = Min(left: (ExactFraction.Of(value: turn) + (((index + 1) < starts.Count) ? starts[(index + 1)] : ExactFraction.Of(value: BigInteger.One))), right: hi);

                if (Compare(left: from, right: to) >= 0) { continue; }

                total += PolynomialIntegral(coefficients: pieces[index], from: (from - ExactFraction.Of(value: turn)), to: (to - ExactFraction.Of(value: turn)));
            }
        }

        return (negative ? (zero - total) : total);

        static int Compare(ExactFraction left, ExactFraction right) => (left.Numerator * right.Denominator).CompareTo(other: (right.Numerator * left.Denominator));
        static ExactFraction Max(ExactFraction left, ExactFraction right) => ((Compare(left: left, right: right) >= 0) ? left : right);
        static ExactFraction Min(ExactFraction left, ExactFraction right) => ((Compare(left: left, right: right) <= 0) ? left : right);
    }
}
