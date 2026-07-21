using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// An exact reduction of one integer-tail equality question to minimality for a degree-one second-order recurrence.
/// </summary>
/// <remarks>
/// The reduced recurrence is
/// <c>(j+Alpha)u_j=(BetaSlope*j+BetaConstant)u_(j-1)+(GammaSlope*j+GammaConstant)u_(j-2)</c>.
/// Its associated continued fraction is the original tail after subtracting its linear base at
/// <see cref="TailIndex"/>. When both recorded characteristic roots are distinct rationals, the 2026
/// Kenison--Klurman--Lefaucheux--Luca--Moree--Ouaknine--Sertöz--Whiteland--Worrell minimality theorem supplies a
/// terminating equality procedure through effective E-function and 1-period relation testing.
/// The initial values use Pincherle's sign convention: <c>u_-1=1</c> and
/// <c>u_0=A_N-IntegerBoundary</c>, so <c>-u_0/u_-1=IntegerBoundary-A_N</c> is the required continued-fraction value.
/// </remarks>
public readonly record struct PolynomialTailMinimalityReduction(
    BigInteger TailIndex,
    BigInteger IntegerBoundary,
    QuadraticSurd Alpha,
    BigInteger BetaSlope,
    BigInteger BetaConstant,
    BigInteger GammaSlope,
    QuadraticSurd GammaConstant,
    QuadraticSurd FirstCharacteristicRoot,
    QuadraticSurd SecondCharacteristicRoot,
    QuadraticSurd InitialMinusOne,
    QuadraticSurd InitialZero
);

public sealed partial class PolynomialContinuedFractionAnalysis {
    /// <summary>
    /// Attempts to reduce <c>s_n=integerBoundary</c> to minimality of a degree-one recurrence with two distinct
    /// rational characteristic roots. Success requires both the characteristic discriminant
    /// <c>p^2+4r</c> and numerator discriminant <c>u^2-4rv</c> to be squares.
    /// </summary>
    public bool TryDegreeOneMinimalityReduction(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        out PolynomialTailMinimalityReduction reduction) {
        ValidateTailIndex(tailIndex);
        reduction = default;

        var numeratorDiscriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * Parameters.NumeratorQuadratic * Parameters.NumeratorConstant)
        );
        if (numeratorDiscriminant < BigInteger.Zero) { return false; }
        var numeratorRoot = BigIntegerMath.SquareRoot(numeratorDiscriminant);
        if ((numeratorRoot * numeratorRoot) != numeratorDiscriminant) { return false; }
        if (!Slope.IsRational) { return false; }

        var twoR = (2 * Parameters.NumeratorQuadratic);
        var firstOffset = QuadraticSurd.Rational(
            Parameters.NumeratorLinear + numeratorRoot,
            twoR
        );
        var secondOffset = QuadraticSurd.Rational(
            Parameters.NumeratorLinear - numeratorRoot,
            twoR
        );
        var alpha = (QuadraticSurd.Rational(tailIndex) + firstOffset);
        var gammaConstant = QuadraticSurd.Rational(Parameters.NumeratorQuadratic) *
            (QuadraticSurd.Rational(tailIndex - 1) + secondOffset);
        var baseAtTail = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var firstRoot = Slope;
        var secondRoot = (QuadraticSurd.Rational(Parameters.Linear) - Slope);

        reduction = new PolynomialTailMinimalityReduction(
            TailIndex: tailIndex,
            IntegerBoundary: integerBoundary,
            Alpha: alpha,
            BetaSlope: Parameters.Linear,
            BetaConstant: ((Parameters.Linear * tailIndex) + Parameters.Constant),
            GammaSlope: Parameters.NumeratorQuadratic,
            GammaConstant: gammaConstant,
            FirstCharacteristicRoot: firstRoot,
            SecondCharacteristicRoot: secondRoot,
            InitialMinusOne: QuadraticSurd.One,
            InitialZero: QuadraticSurd.Rational(baseAtTail - integerBoundary)
        );

        if (VerifyDegreeOneMinimalityReduction(reduction)) { return true; }
        reduction = default;
        return false;
    }

    /// <summary>Rechecks every rational coefficient and shifted polynomial identity in a minimality reduction.</summary>
    public bool VerifyDegreeOneMinimalityReduction(PolynomialTailMinimalityReduction reduction) {
        if ((reduction.TailIndex <= BigInteger.Zero) ||
            !reduction.Alpha.IsRational || !reduction.GammaConstant.IsRational ||
            !reduction.FirstCharacteristicRoot.IsRational || !reduction.SecondCharacteristicRoot.IsRational ||
            !reduction.InitialMinusOne.IsRational || !reduction.InitialZero.IsRational ||
            (reduction.BetaSlope != Parameters.Linear) ||
            (reduction.GammaSlope != Parameters.NumeratorQuadratic) ||
            (reduction.BetaConstant != ((Parameters.Linear * reduction.TailIndex) + Parameters.Constant)) ||
            (reduction.FirstCharacteristicRoot == reduction.SecondCharacteristicRoot) ||
            (reduction.InitialMinusOne != QuadraticSurd.One)) {
            return false;
        }

        var firstRoot = reduction.FirstCharacteristicRoot;
        var secondRoot = reduction.SecondCharacteristicRoot;
        if ((firstRoot + secondRoot) != QuadraticSurd.Rational(Parameters.Linear) ||
            (firstRoot * secondRoot) != -QuadraticSurd.Rational(Parameters.NumeratorQuadratic)) {
            return false;
        }

        var shift = (reduction.TailIndex - 1);
        var shiftedLinear = ((2 * Parameters.NumeratorQuadratic * shift) + Parameters.NumeratorLinear);
        var shiftedConstant = (
            (Parameters.NumeratorQuadratic * shift * shift) +
            (Parameters.NumeratorLinear * shift) +
            Parameters.NumeratorConstant
        );
        var expectedInitialZero = QuadraticSurd.Rational(
            ((Parameters.Linear * reduction.TailIndex) + Parameters.Constant) - reduction.IntegerBoundary
        );

        return
            (reduction.InitialZero == expectedInitialZero) &&
            (QuadraticSurd.Rational(reduction.GammaSlope) * (reduction.Alpha - QuadraticSurd.One) +
                reduction.GammaConstant) == QuadraticSurd.Rational(shiftedLinear) &&
            (reduction.GammaConstant * (reduction.Alpha - QuadraticSurd.One)) ==
                QuadraticSurd.Rational(shiftedConstant);
    }
}
