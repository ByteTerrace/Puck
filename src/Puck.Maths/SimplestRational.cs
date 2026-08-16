using System.Numerics;

namespace Puck.Maths;

/// <summary>Locates the fraction with the smallest denominator inside an exact real interval.</summary>
/// <remarks>
/// The search walks the Stern–Brocot tree by stripping one shared continued-fraction digit per step, in exact
/// <see cref="QuadraticSurd"/> arithmetic; it terminates because two distinct exact values share only finitely many
/// leading digits. The result is the unique minimal-denominator fraction in the interval, which is what makes it the
/// first index at which two Beatty sequences with slopes on either side of it can disagree — the use
/// <see cref="BeattyQuantization.FirstFloorDisagreement"/> puts it to.
/// </remarks>
public static class SimplestRational {
    /// <summary>Returns the fraction with the smallest denominator strictly between <paramref name="low"/> and <paramref name="high"/>; among fractions with that denominator, the one with the least numerator.</summary>
    /// <param name="low">The exact lower endpoint, excluded from the interval.</param>
    /// <param name="high">The exact upper endpoint, excluded from the interval; it must be strictly greater than <paramref name="low"/>.</param>
    /// <returns>The numerator and positive denominator of the simplest fraction, in lowest terms.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="high"/> is not strictly greater than <paramref name="low"/>.</exception>
    public static (BigInteger Numerator, BigInteger Denominator) InOpenInterval(QuadraticSurd low, QuadraticSurd high) {
        if (low.CompareTo(other: high) >= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(high),
                message: "the interval is empty: high must be strictly greater than low"
            );
        }

        var digits = new List<BigInteger>();
        var lowCursor = low;
        var highCursor = high;
        BigInteger numerator;
        BigInteger denominator;

        while (true) {
            var floorLow = lowCursor.Floor();
            var candidate = (floorLow + BigInteger.One);

            // The smallest integer strictly above the lower endpoint; if it is inside, denominator one wins.
            if (QuadraticSurd.Rational(value: candidate) < highCursor) {
                (numerator, denominator) = (candidate, BigInteger.One);

                break;
            }

            var lowFraction = (lowCursor - QuadraticSurd.Rational(value: floorLow));
            var highFraction = (highCursor - QuadraticSurd.Rational(value: floorLow));

            digits.Add(item: floorLow);

            if (lowFraction.Sign == 0) {
                // The lower endpoint is exactly an integer, so inversion sends the interval to (1/highFraction, ∞),
                // where the simplest value is the smallest integer strictly above the finite endpoint.
                var inverted = (QuadraticSurd.One / highFraction);

                (numerator, denominator) = ((inverted.Floor() + BigInteger.One), BigInteger.One);

                break;
            }

            (lowCursor, highCursor) = ((QuadraticSurd.One / highFraction), (QuadraticSurd.One / lowFraction));
        }

        for (var index = (digits.Count - 1); (index >= 0); --index) {
            (numerator, denominator) = (((digits[index] * numerator) + denominator), numerator);
        }

        return (numerator, denominator);
    }
}
