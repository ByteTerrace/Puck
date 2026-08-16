using System.Numerics;
using Puck.Maths.Research;

namespace Puck.Maths;

/// <summary>A positive unit <c>X + Y*sqrt(D)</c> of norm one in a real quadratic order.</summary>
/// <param name="Radicand">The positive non-square integer <c>D</c>.</param>
/// <param name="X">The positive integer <c>X</c> in <c>X^2-DY^2=1</c>.</param>
/// <param name="Y">The positive integer <c>Y</c> in <c>X^2-DY^2=1</c>.</param>
public readonly record struct PellUnit(BigInteger Radicand, BigInteger X, BigInteger Y) {
    /// <summary>Multiplies <c>x+y*sqrt(D)</c> by the inverse unit <c>X-Y*sqrt(D)</c>.</summary>
    public (BigInteger X, BigInteger Y) Divide(BigInteger x, BigInteger y) => (
        ((X * x) - ((Radicand * Y) * y)),
        ((-Y * x) + (X * y))
    );
    /// <summary>Multiplies <c>x+y*sqrt(D)</c> by this unit.</summary>
    public (BigInteger X, BigInteger Y) Multiply(BigInteger x, BigInteger y) => (
        ((X * x) + ((Radicand * Y) * y)),
        ((Y * x) + (X * y))
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
                    ((resultX * factorX) + ((Radicand * resultY) * factorY)),
                    ((resultX * factorY) + (resultY * factorX))
                );
            }

            remaining >>= 1;
            if (remaining == 0) { continue; }

            (factorX, factorY) = (
                ((factorX * factorX) + ((Radicand * factorY) * factorY)),
                ((2 * factorX) * factorY)
            );
        }

        return new PellUnit(
            Radicand: Radicand,
            X: resultX,
            Y: resultY
        );
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
    private static void ValidateRadicand(BigInteger radicand) {
        if (radicand <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radicand),
                message: "the Pell radicand must be positive"
            );
        }

        var root = BigIntegerFunctions.SquareRoot(value: radicand);

        if ((root * root) == radicand) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radicand),
                message: "the Pell radicand must not be a perfect square"
            );
        }
    }

    /// <summary>Returns the fundamental positive solution of <c>X^2-DY^2=1</c> — the fundamental norm-one unit.</summary>
    /// <remarks>
    /// <para>
    /// Norm one is stronger than "the fundamental unit", and the distinction is load-bearing: whenever the minimal
    /// solution of <c>X^2-DY^2=+-1</c> has norm minus one, this method returns its square. At <c>D=13</c> the minimal
    /// solution is <c>18^2-13*5^2=-1</c> and this returns <c>649^2-13*180^2=1</c>. Deriving a minimal <c>+-1</c> or
    /// <c>+-4</c> answer from what this returns is therefore wrong. Callers that need determinant one —
    /// <see cref="ResidueCycle(PellUnit, BigInteger, BigInteger, BigInteger)"/> and the orbit box of
    /// <see cref="OrbitRepresentatives(BigInteger, BigInteger)"/> — need exactly this.
    /// </para>
    /// <para>
    /// <c>Z[sqrt(D)]</c> is the order of discriminant <c>4D</c>, so the work is delegated to
    /// <see cref="QuadraticNormEquation"/>: the substitution <c>X = 2a</c>, <c>Y = b</c> carries that order's unit
    /// equation <c>X^2-4D*Y^2=+-4</c> onto <c>a^2-D*b^2=+-1</c>, and squaring covers the norm-minus-one branch.
    /// </para>
    /// </remarks>
    /// <param name="radicand">The integer <c>D</c>; it must be positive and not a square.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is not positive or is a square.</exception>
    public static PellUnit FundamentalUnit(BigInteger radicand) {
        ValidateRadicand(radicand: radicand);

        var solution = QuadraticNormEquation.FundamentalUnit(discriminant: (4 * radicand));
        // X is even here: X^2 = 4D*Y^2 +- 4 is divisible by four.
        var x = (solution.X / 2);

        if (0 < solution.NormSign) {
            return new PellUnit(
                Radicand: radicand,
                X: x,
                Y: solution.Y
            );
        }

        return new PellUnit(
            Radicand: radicand,
            X: ((x * x) + ((radicand * solution.Y) * solution.Y)),
            Y: ((2 * x) * solution.Y)
        );
    }
    /// <summary>
    /// Returns a finite, possibly redundant set of bounded representatives whose norm-one unit orbits contain every
    /// integer solution of <c>X^2-DY^2=N</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If <c>epsilon=U+V*sqrt(D)</c> is the fundamental unit, every solution can be multiplied by a power of
    /// <c>epsilon</c> until both embeddings have magnitude below <c>sqrt(|N|*epsilon)</c>. Since
    /// <c>epsilon&lt;2U</c>, that orbit contains a representative satisfying
    /// <c>X^2&lt;2|N|U</c> and <c>DY^2&lt;2|N|U</c>. Exhausting this explicit box therefore meets every orbit.
    /// Representatives are not quotient-deduplicated; redundancy keeps the certificate simple and independently
    /// checkable.
    /// </para>
    /// <para>
    /// Cost follows the box: the walk is linear in <c>U</c>, which grows exponentially in <c>sqrt(D)</c>, so a radicand
    /// with a large fundamental unit puts this method out of reach however small <c>N</c> is. That price buys every
    /// orbit; a caller that needs one solution of a given norm — one element of the order — wants
    /// <see cref="QuadraticNormEquation"/> instead, which reaches it along the ideal's continued fraction and never
    /// enumerates the box.
    /// </para>
    /// </remarks>
    /// <param name="radicand">The positive non-square integer <c>D</c>.</param>
    /// <param name="norm">The integer <c>N</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is not positive or is a square.</exception>
    public static IReadOnlyList<GeneralizedPellRepresentative> OrbitRepresentatives(
        BigInteger radicand,
        BigInteger norm) {
        var unit = FundamentalUnit(radicand: radicand);

        if (norm.IsZero) {
            return [new GeneralizedPellRepresentative(
                    X: BigInteger.Zero,
                    Y: BigInteger.Zero
                )];
        }

        var strictSquareCeiling = (((2 * BigInteger.Abs(value: norm)) * unit.X) - 1);
        var xBound = BigIntegerFunctions.SquareRoot(value: strictSquareCeiling);
        var yBound = BigIntegerFunctions.SquareRoot(value: (strictSquareCeiling / radicand));
        var representatives = new List<GeneralizedPellRepresentative>();

        for (var y = -yBound; (y <= yBound); ++y) {
            var xSquare = (norm + ((radicand * y) * y));

            if (xSquare.Sign < 0) { continue; }

            var x = BigIntegerFunctions.SquareRoot(value: xSquare);

            if ((x * x) != xSquare) { continue; }

            representatives.Add(item: new GeneralizedPellRepresentative(
                X: x,
                Y: y
            ));
            if (!x.IsZero) {
                representatives.Add(item: new GeneralizedPellRepresentative(
                    X: -x,
                    Y: y
                ));
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

        var start = new PellResidue(
            X: x.FloorModulo(modulus: modulus),
            Y: y.FloorModulo(modulus: modulus)
        );
        var current = start;
        var cycle = new List<PellResidue>();

        do {
            cycle.Add(item: current);
            var nextX = ((unit.X * current.X) + ((unit.Radicand * unit.Y) * current.Y));
            var nextY = ((unit.Y * current.X) + (unit.X * current.Y));

            current = new PellResidue(
                X: nextX.FloorModulo(modulus: modulus),
                Y: nextY.FloorModulo(modulus: modulus)
            );
        } while (current != start);

        return cycle;
    }
}
