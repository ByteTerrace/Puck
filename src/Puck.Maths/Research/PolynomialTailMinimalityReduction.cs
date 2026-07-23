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
/// The initial values include the equivalence-transformation scale: <c>u_-1=Alpha</c> and
/// <c>u_0=A_N-IntegerBoundary</c>, so <c>-u_0/u_-1=(IntegerBoundary-A_N)/Alpha</c> is the normalized
/// continued-fraction value in Pincherle's convention.
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

/// <summary>
/// An exact reduction of one integer-tail equality question to a linear relation between 1-periods obtained from
/// Gauss hypergeometric functions.
/// </summary>
/// <remarks>
/// The parameters satisfy the Lorentzen--Waadeland/Kenison transformation
/// <c>a=(ell^2*Alpha-ell*BetaConstant-GammaConstant)/(ell*(ell-mu))</c>,
/// <c>b=Alpha-1</c>, <c>c=a+GammaConstant/GammaSlope</c>, and <c>x=ell/mu</c>, where <c>mu</c> is the dominant
/// characteristic root and <c>ell</c> the other root.  Rational <c>a,b,c</c> and algebraic <c>x</c> make the resulting
/// Euler integral a 1-period.  The effective 1-period relation theorem therefore decides the equality, although this
/// record deliberately does not claim to implement that external algebraic-geometry algorithm.
/// In the unshifted hypergeometric form the requested relation is
/// <c>c*2F1(a,b;c;x)=HypergeometricRatioTarget*2F1(a,b+1;c+1;x)</c>.
/// The continued-fraction equivalence prefactor is <c>mu/Alpha</c>, so the target contains one power of
/// <c>mu</c>.  This follows directly from the equivalence transformation and is independently checked against
/// convergents by the verifier.
/// </remarks>
public readonly record struct PolynomialTailOnePeriodReduction(
    BigInteger TailIndex,
    BigInteger IntegerBoundary,
    QuadraticSurd Alpha,
    BigInteger BetaSlope,
    BigInteger BetaConstant,
    BigInteger GammaSlope,
    QuadraticSurd GammaConstant,
    QuadraticSurd DominantCharacteristicRoot,
    QuadraticSurd OtherCharacteristicRoot,
    QuadraticSurd HypergeometricA,
    QuadraticSurd HypergeometricB,
    QuadraticSurd HypergeometricC,
    QuadraticSurd HypergeometricArgument,
    QuadraticSurd HypergeometricRatioTarget,
    BigInteger EulerShift,
    QuadraticSurd InitialMinusOne,
    QuadraticSurd InitialZero
);

public sealed partial class PolynomialContinuedFractionAnalysis {
    /// <summary>
    /// Attempts to reduce <c>s_n=integerBoundary</c> to equality of 1-periods through the hypergeometric form of a
    /// degree-one recurrence.  The numerator discriminant must be a rational square, and the transformed parameters
    /// <c>a,b,c</c> must be rational.  Besides the former double-square branch, this includes irrational characteristic
    /// roots whenever <c>p*(u-r)=2*r*q</c>.
    /// </summary>
    public bool TryOnePeriodEqualityReduction(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        out PolynomialTailOnePeriodReduction reduction) {
        ValidateTailIndex(tailIndex);
        reduction = default;

        if (!TryFactoredDegreeOneParameters(
            tailIndex,
            out var alpha,
            out var gammaConstant)) {
            return false;
        }

        var p = QuadraticSurd.Rational(Parameters.Linear);
        var r = QuadraticSurd.Rational(Parameters.NumeratorQuadratic);
        var betaConstant = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var beta = QuadraticSurd.Rational(betaConstant);
        var dominant = Slope;
        var other = (p - dominant);
        var parameterA = (
            ((other * other * alpha) - (other * beta) - gammaConstant) /
            (other * (other - dominant))
        );
        var parameterB = (alpha - QuadraticSurd.One);
        var parameterC = (parameterA + (gammaConstant / r));
        var argument = (other / dominant);
        var eulerShift = EulerRegularizationShift(parameterB, parameterC);
        var baseAtTail = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var initialZero = QuadraticSurd.Rational(baseAtTail - integerBoundary);
        var initialMinusOne = alpha;
        var ratioTarget = (
            (beta / dominant) -
            ((alpha * initialZero) / (dominant * initialMinusOne))
        );

        reduction = new PolynomialTailOnePeriodReduction(
            TailIndex: tailIndex,
            IntegerBoundary: integerBoundary,
            Alpha: alpha,
            BetaSlope: Parameters.Linear,
            BetaConstant: betaConstant,
            GammaSlope: Parameters.NumeratorQuadratic,
            GammaConstant: gammaConstant,
            DominantCharacteristicRoot: dominant,
            OtherCharacteristicRoot: other,
            HypergeometricA: parameterA,
            HypergeometricB: parameterB,
            HypergeometricC: parameterC,
            HypergeometricArgument: argument,
            HypergeometricRatioTarget: ratioTarget,
            EulerShift: eulerShift,
            InitialMinusOne: initialMinusOne,
            InitialZero: initialZero
        );

        if (VerifyOnePeriodEqualityReduction(reduction)) { return true; }
        reduction = default;
        return false;
    }

    /// <summary>Rechecks the factorization and every hypergeometric parameter in a 1-period reduction.</summary>
    public bool VerifyOnePeriodEqualityReduction(PolynomialTailOnePeriodReduction reduction) {
        if ((reduction.TailIndex <= BigInteger.Zero) ||
            !reduction.Alpha.IsRational || !reduction.GammaConstant.IsRational ||
            !reduction.HypergeometricA.IsRational || !reduction.HypergeometricB.IsRational ||
            !reduction.HypergeometricC.IsRational ||
            !BelongsToCharacteristicField(reduction.OtherCharacteristicRoot) ||
            !BelongsToCharacteristicField(reduction.HypergeometricArgument) ||
            !BelongsToCharacteristicField(reduction.HypergeometricRatioTarget) ||
            (reduction.EulerShift < BigInteger.Zero) || !reduction.EulerShift.IsEven ||
            (reduction.BetaSlope != Parameters.Linear) ||
            (reduction.GammaSlope != Parameters.NumeratorQuadratic) ||
            (reduction.BetaConstant != ((Parameters.Linear * reduction.TailIndex) + Parameters.Constant)) ||
            (reduction.DominantCharacteristicRoot != Slope) ||
            (reduction.OtherCharacteristicRoot == reduction.DominantCharacteristicRoot) ||
            (reduction.InitialMinusOne != reduction.Alpha)) {
            return false;
        }

        var p = QuadraticSurd.Rational(Parameters.Linear);
        var r = QuadraticSurd.Rational(Parameters.NumeratorQuadratic);
        var dominant = reduction.DominantCharacteristicRoot;
        var other = reduction.OtherCharacteristicRoot;
        var expectedInitialZero = QuadraticSurd.Rational(
            ((Parameters.Linear * reduction.TailIndex) + Parameters.Constant) - reduction.IntegerBoundary
        );
        var shift = (reduction.TailIndex - 1);
        var shiftedLinear = ((2 * Parameters.NumeratorQuadratic * shift) + Parameters.NumeratorLinear);
        var shiftedConstant = (
            (Parameters.NumeratorQuadratic * shift * shift) +
            (Parameters.NumeratorLinear * shift) +
            Parameters.NumeratorConstant
        );
        var expectedA = (
            ((other * other * reduction.Alpha) -
                (other * QuadraticSurd.Rational(reduction.BetaConstant)) - reduction.GammaConstant) /
            (other * (other - dominant))
        );
        var numeratorDiscriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * Parameters.NumeratorQuadratic * Parameters.NumeratorConstant)
        );
        var numeratorRoot = BigIntegerFunctions.SquareRoot(numeratorDiscriminant);
        var alignmentResidual = (
            (Parameters.Linear * (Parameters.NumeratorLinear - Parameters.NumeratorQuadratic)) -
            (2 * Parameters.NumeratorQuadratic * Parameters.Constant)
        );
        var decomposedA =
            QuadraticSurd.Rational(
                numeratorRoot + Parameters.NumeratorQuadratic,
                2 * Parameters.NumeratorQuadratic
            ) +
            QuadraticSurd.Rational(
                alignmentResidual,
                2 * Parameters.NumeratorQuadratic
            ) / (other - dominant);
        var expectedEulerShift = EulerRegularizationShift(
            reduction.HypergeometricB,
            reduction.HypergeometricC
        );
        var shiftedB = (
            reduction.HypergeometricB + QuadraticSurd.Rational(reduction.EulerShift)
        );
        var shiftedCMinusB = (
            reduction.HypergeometricC - reduction.HypergeometricB +
            QuadraticSurd.Rational(reduction.EulerShift)
        );
        var expectedRatioTarget = (
            (QuadraticSurd.Rational(reduction.BetaConstant) / dominant) -
            ((reduction.Alpha * reduction.InitialZero) /
                (dominant * reduction.InitialMinusOne))
        );

        return
            (reduction.InitialZero == expectedInitialZero) &&
            (dominant + other == p) &&
            (dominant * other == -r) &&
            (reduction.HypergeometricArgument == (other / dominant)) &&
            (reduction.HypergeometricRatioTarget == expectedRatioTarget) &&
            (reduction.HypergeometricArgument.Abs() < QuadraticSurd.One) &&
            (reduction.HypergeometricA == expectedA) &&
            (reduction.HypergeometricA == decomposedA) &&
            (reduction.HypergeometricB == (reduction.Alpha - QuadraticSurd.One)) &&
            (reduction.HypergeometricC ==
                (reduction.HypergeometricA + (reduction.GammaConstant / r))) &&
            (reduction.EulerShift == expectedEulerShift) &&
            (shiftedB.Sign > 0) && (shiftedCMinusB.Sign > 0) &&
            (r * (reduction.Alpha - QuadraticSurd.One) + reduction.GammaConstant) ==
                QuadraticSurd.Rational(shiftedLinear) &&
            (reduction.GammaConstant * (reduction.Alpha - QuadraticSurd.One)) ==
                QuadraticSurd.Rational(shiftedConstant);
    }

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

        if (!TryFactoredDegreeOneParameters(
            tailIndex,
            out var alpha,
            out var gammaConstant)) {
            return false;
        }
        if (!Slope.IsRational) { return false; }
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
            InitialMinusOne: alpha,
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
            (reduction.InitialMinusOne != reduction.Alpha)) {
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

    private bool TryFactoredDegreeOneParameters(
        BigInteger tailIndex,
        out QuadraticSurd alpha,
        out QuadraticSurd gammaConstant) {
        alpha = default;
        gammaConstant = default;
        var numeratorDiscriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * Parameters.NumeratorQuadratic * Parameters.NumeratorConstant)
        );
        if (numeratorDiscriminant < BigInteger.Zero) { return false; }
        var numeratorRoot = BigIntegerFunctions.SquareRoot(numeratorDiscriminant);
        if ((numeratorRoot * numeratorRoot) != numeratorDiscriminant) { return false; }

        var twoR = (2 * Parameters.NumeratorQuadratic);
        var firstOffset = QuadraticSurd.Rational(
            Parameters.NumeratorLinear + numeratorRoot,
            twoR
        );
        var secondOffset = QuadraticSurd.Rational(
            Parameters.NumeratorLinear - numeratorRoot,
            twoR
        );
        alpha = (QuadraticSurd.Rational(tailIndex) + firstOffset);
        gammaConstant = QuadraticSurd.Rational(Parameters.NumeratorQuadratic) *
            (QuadraticSurd.Rational(tailIndex - 1) + secondOffset);
        return true;
    }

    private static BigInteger EulerRegularizationShift(QuadraticSurd parameterB, QuadraticSurd parameterC) {
        var shift = BigInteger.Max(
            BigInteger.Zero,
            BigInteger.Max(
                (-parameterB).Floor() + 1,
                (parameterB - parameterC).Floor() + 1
            )
        );
        return shift.IsEven ? shift : (shift + 1);
    }
}
