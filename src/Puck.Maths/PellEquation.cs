using System.Numerics;

namespace Puck.Maths;

/// <summary>A positive unit <c>X + Y*sqrt(D)</c> of norm one in a real quadratic order.</summary>
/// <param name="Radicand">The positive non-square integer <c>D</c>.</param>
/// <param name="X">The positive integer <c>X</c> in <c>X^2-DY^2=1</c>.</param>
/// <param name="Y">The positive integer <c>Y</c> in <c>X^2-DY^2=1</c>.</param>
public readonly record struct PellUnit(BigInteger Radicand, BigInteger X, BigInteger Y) {
    /// <summary>Multiplies <c>x+y*sqrt(D)</c> by this unit.</summary>
    public (BigInteger X, BigInteger Y) Multiply(BigInteger x, BigInteger y) => (
        ((X * x) + (Radicand * Y * y)),
        ((Y * x) + (X * y))
    );

    /// <summary>Multiplies <c>x+y*sqrt(D)</c> by the inverse unit <c>X-Y*sqrt(D)</c>.</summary>
    public (BigInteger X, BigInteger Y) Divide(BigInteger x, BigInteger y) => (
        ((X * x) - (Radicand * Y * y)),
        ((-Y * x) + (X * y))
    );

    /// <summary>Returns this unit raised to a non-negative integer power.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exponent"/> is negative.</exception>
    public PellUnit Power(int exponent) {
        ArgumentOutOfRangeException.ThrowIfNegative(exponent);

        var resultX = BigInteger.One;
        var resultY = BigInteger.Zero;
        var factorX = X;
        var factorY = Y;
        var remaining = exponent;

        while (remaining > 0) {
            if ((remaining & 1) != 0) {
                (resultX, resultY) = (
                    ((resultX * factorX) + (Radicand * resultY * factorY)),
                    ((resultX * factorY) + (resultY * factorX))
                );
            }

            remaining >>= 1;
            if (remaining == 0) { continue; }

            (factorX, factorY) = (
                ((factorX * factorX) + (Radicand * factorY * factorY)),
                (2 * factorX * factorY)
            );
        }

        return new PellUnit(Radicand: Radicand, X: resultX, Y: resultY);
    }
}

/// <summary>A bounded representative of an orbit of the generalized Pell equation <c>X^2-DY^2=N</c>.</summary>
/// <param name="X">The rational coefficient.</param>
/// <param name="Y">The square-root coefficient.</param>
public readonly record struct GeneralizedPellRepresentative(BigInteger X, BigInteger Y);

/// <summary>One residue pair in a norm-one unit orbit modulo a positive integer.</summary>
/// <param name="X">The canonical residue of the rational coefficient.</param>
/// <param name="Y">The canonical residue of the square-root coefficient.</param>
public readonly record struct PellResidue(BigInteger X, BigInteger Y);

/// <summary>Exact continued-fraction and finite-orbit operations for Pell equations.</summary>
public static class PellEquation {
    /// <summary>Returns the fundamental positive solution of <c>X^2-DY^2=1</c>.</summary>
    /// <param name="radicand">The integer <c>D</c>; it must be positive and not a square.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is not positive or is a square.</exception>
    public static PellUnit FundamentalUnit(BigInteger radicand) {
        ValidateRadicand(radicand: radicand);

        var root = BigIntegerMath.SquareRoot(value: radicand);
        var remainder = BigInteger.Zero;
        var denominator = BigInteger.One;
        var quotient = root;
        var previousPreviousNumerator = BigInteger.Zero;
        var previousNumerator = BigInteger.One;
        var previousPreviousDenominator = BigInteger.One;
        var previousDenominator = BigInteger.Zero;

        while (true) {
            var numerator = ((quotient * previousNumerator) + previousPreviousNumerator);
            var denominatorConvergent = ((quotient * previousDenominator) + previousPreviousDenominator);

            if (((numerator * numerator) - (radicand * denominatorConvergent * denominatorConvergent)) == BigInteger.One) {
                return new PellUnit(
                    Radicand: radicand,
                    X: numerator,
                    Y: denominatorConvergent
                );
            }

            previousPreviousNumerator = previousNumerator;
            previousNumerator = numerator;
            previousPreviousDenominator = previousDenominator;
            previousDenominator = denominatorConvergent;
            remainder = ((denominator * quotient) - remainder);
            denominator = ((radicand - (remainder * remainder)) / denominator);
            quotient = ((root + remainder) / denominator);
        }
    }

    /// <summary>
    /// Returns a finite, possibly redundant set of bounded representatives whose norm-one unit orbits contain every
    /// integer solution of <c>X^2-DY^2=N</c>.
    /// </summary>
    /// <remarks>
    /// If <c>epsilon=U+V*sqrt(D)</c> is the fundamental unit, every solution can be multiplied by a power of
    /// <c>epsilon</c> until both embeddings have magnitude below <c>sqrt(|N|*epsilon)</c>. Since
    /// <c>epsilon&lt;2U</c>, that orbit contains a representative satisfying
    /// <c>X^2&lt;2|N|U</c> and <c>DY^2&lt;2|N|U</c>. Exhausting this explicit box therefore meets every orbit.
    /// Representatives are not quotient-deduplicated; redundancy keeps the certificate simple and independently
    /// checkable.
    /// </remarks>
    /// <param name="radicand">The positive non-square integer <c>D</c>.</param>
    /// <param name="norm">The integer <c>N</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is not positive or is a square.</exception>
    public static IReadOnlyList<GeneralizedPellRepresentative> OrbitRepresentatives(
        BigInteger radicand,
        BigInteger norm) {
        var unit = FundamentalUnit(radicand: radicand);

        if (norm.IsZero) {
            return [new GeneralizedPellRepresentative(X: BigInteger.Zero, Y: BigInteger.Zero)];
        }

        var strictSquareCeiling = ((2 * BigInteger.Abs(norm) * unit.X) - 1);
        var xBound = BigIntegerMath.SquareRoot(value: strictSquareCeiling);
        var yBound = BigIntegerMath.SquareRoot(value: (strictSquareCeiling / radicand));
        var representatives = new List<GeneralizedPellRepresentative>();

        for (var y = -yBound; y <= yBound; ++y) {
            var xSquare = (norm + (radicand * y * y));
            if (xSquare.Sign < 0) { continue; }

            var x = BigIntegerMath.SquareRoot(value: xSquare);
            if ((x * x) != xSquare) { continue; }

            representatives.Add(new GeneralizedPellRepresentative(X: x, Y: y));
            if (!x.IsZero) {
                representatives.Add(new GeneralizedPellRepresentative(X: -x, Y: y));
            }
        }

        return representatives;
    }

    /// <summary>
    /// Returns the complete residue cycle of <c>x+y*sqrt(D)</c> under multiplication by a norm-one unit modulo
    /// <paramref name="modulus"/>.
    /// </summary>
    /// <remarks>
    /// The multiplication matrix has determinant one, so it permutes the finite residue-pair set. The orbit is
    /// therefore purely periodic and returns to its starting pair without a preperiod.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is not positive.</exception>
    public static IReadOnlyList<PellResidue> ResidueCycle(
        PellUnit unit,
        BigInteger x,
        BigInteger y,
        BigInteger modulus) {
        if (modulus <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(modulus),
                message: "the residue modulus must be positive"
            );
        }

        var start = new PellResidue(X: PositiveRemainder(x, modulus), Y: PositiveRemainder(y, modulus));
        var current = start;
        var cycle = new List<PellResidue>();

        do {
            cycle.Add(current);
            var nextX = ((unit.X * current.X) + (unit.Radicand * unit.Y * current.Y));
            var nextY = ((unit.Y * current.X) + (unit.X * current.Y));
            current = new PellResidue(
                X: PositiveRemainder(nextX, modulus),
                Y: PositiveRemainder(nextY, modulus)
            );
        } while (current != start);

        return cycle;
    }

    private static void ValidateRadicand(BigInteger radicand) {
        if (radicand <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radicand),
                message: "the Pell radicand must be positive"
            );
        }

        var root = BigIntegerMath.SquareRoot(value: radicand);
        if ((root * root) == radicand) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radicand),
                message: "the Pell radicand must not be a perfect square"
            );
        }
    }

    private static BigInteger PositiveRemainder(BigInteger value, BigInteger modulus) {
        var remainder = (value % modulus);

        return (remainder.Sign < 0) ? (remainder + modulus) : remainder;
    }
}
