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
    RealQuadratic Alpha,
    BigInteger BetaSlope,
    BigInteger BetaConstant,
    BigInteger GammaSlope,
    RealQuadratic GammaConstant,
    RealQuadratic FirstCharacteristicRoot,
    RealQuadratic SecondCharacteristicRoot,
    RealQuadratic InitialMinusOne,
    RealQuadratic InitialZero
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
/// <c>mu</c>.  This follows directly from the equivalence transformation, which is the whole of its evidence:
/// <see cref="PolynomialContinuedFractionAnalysis.VerifyOnePeriodEqualityReduction"/> rechecks the factorization and
/// every hypergeometric parameter, but nothing compares the prefactor against actual convergents.
/// </remarks>
public readonly record struct PolynomialTailOnePeriodReduction(
    BigInteger TailIndex,
    BigInteger IntegerBoundary,
    RealQuadratic Alpha,
    BigInteger BetaSlope,
    BigInteger BetaConstant,
    BigInteger GammaSlope,
    RealQuadratic GammaConstant,
    RealQuadratic DominantCharacteristicRoot,
    RealQuadratic OtherCharacteristicRoot,
    RealQuadratic HypergeometricA,
    RealQuadratic HypergeometricB,
    RealQuadratic HypergeometricC,
    RealQuadratic HypergeometricArgument,
    RealQuadratic HypergeometricRatioTarget,
    BigInteger EulerShift,
    RealQuadratic InitialMinusOne,
    RealQuadratic InitialZero
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
        PolynomialTailIndex.RequirePositive(tailIndex: tailIndex);
        reduction = default;

        if (!TryFactoredDegreeOneParameters(
            alpha: out var alpha,
            gammaConstant: out var gammaConstant,
            tailIndex: tailIndex
        )) {
            return false;
        }

        var p = RealQuadratic.Rational(value: Parameters.Linear);
        var r = RealQuadratic.Rational(value: Parameters.NumeratorQuadratic);
        var betaConstant = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var beta = RealQuadratic.Rational(value: betaConstant);
        var dominant = Slope;
        var other = (p - dominant);
        var parameterA = (
            ((((other * other) * alpha) - (other * beta)) - gammaConstant) /
            (other * (other - dominant))
        );
        var parameterB = (alpha - RealQuadratic.One);
        var parameterC = (parameterA + (gammaConstant / r));
        var argument = (other / dominant);
        var eulerShift = EulerRegularizationShift(
            parameterB: parameterB,
            parameterC: parameterC
        );
        var baseAtTail = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var initialZero = RealQuadratic.Rational(value: (baseAtTail - integerBoundary));
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

        if (VerifyOnePeriodEqualityReduction(reduction: reduction)) { return true; }
        reduction = default;
        return false;
    }
    /// <summary>Rechecks the factorization and every hypergeometric parameter in a 1-period reduction.</summary>
    public bool VerifyOnePeriodEqualityReduction(PolynomialTailOnePeriodReduction reduction) {
        if (
            (reduction.TailIndex <= BigInteger.Zero) ||
            !reduction.Alpha.IsRational ||
            !reduction.GammaConstant.IsRational ||
            !reduction.HypergeometricA.IsRational ||
            !reduction.HypergeometricB.IsRational ||
            !reduction.HypergeometricC.IsRational ||
            !BelongsToCharacteristicField(value: reduction.OtherCharacteristicRoot) ||
            !BelongsToCharacteristicField(value: reduction.HypergeometricArgument) ||
            !BelongsToCharacteristicField(value: reduction.HypergeometricRatioTarget) ||
            (reduction.EulerShift < BigInteger.Zero) ||
            !reduction.EulerShift.IsEven ||
            (reduction.BetaSlope != Parameters.Linear) ||
            (reduction.GammaSlope != Parameters.NumeratorQuadratic) ||
            (reduction.BetaConstant != ((Parameters.Linear * reduction.TailIndex) + Parameters.Constant)) ||
            (reduction.DominantCharacteristicRoot != Slope) ||
            (reduction.OtherCharacteristicRoot == reduction.DominantCharacteristicRoot) ||
            (reduction.InitialMinusOne != reduction.Alpha)
        ) {
            return false;
        }

        var p = RealQuadratic.Rational(value: Parameters.Linear);
        var r = RealQuadratic.Rational(value: Parameters.NumeratorQuadratic);
        var dominant = reduction.DominantCharacteristicRoot;
        var other = reduction.OtherCharacteristicRoot;
        var expectedInitialZero = RealQuadratic.Rational(value: (((Parameters.Linear * reduction.TailIndex) + Parameters.Constant) - reduction.IntegerBoundary));
        var shift = (reduction.TailIndex - 1);
        var shiftedLinear = (((2 * Parameters.NumeratorQuadratic) * shift) + Parameters.NumeratorLinear);
        var shiftedConstant = (
            (((Parameters.NumeratorQuadratic * shift) * shift) +
            (Parameters.NumeratorLinear * shift)) +
            Parameters.NumeratorConstant
        );
        var expectedA = (
            ((((other * other) * reduction.Alpha) -
                (other * RealQuadratic.Rational(value: reduction.BetaConstant))) - reduction.GammaConstant) /
            (other * (other - dominant))
        );
        var numeratorDiscriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            ((4 * Parameters.NumeratorQuadratic) * Parameters.NumeratorConstant)
        );
        var numeratorRoot = BigIntegerFunctions.SquareRoot(value: numeratorDiscriminant);
        var alignmentResidual = (
            (Parameters.Linear * (Parameters.NumeratorLinear - Parameters.NumeratorQuadratic)) -
            ((2 * Parameters.NumeratorQuadratic) * Parameters.Constant)
        );
        var decomposedA =
            (RealQuadratic.Rational(
            denominator: (2 * Parameters.NumeratorQuadratic),
            numerator: (numeratorRoot + Parameters.NumeratorQuadratic)
        ) +
            (RealQuadratic.Rational(
            denominator: (2 * Parameters.NumeratorQuadratic),
            numerator: alignmentResidual
        ) / (other - dominant)));
        var expectedEulerShift = EulerRegularizationShift(
            parameterB: reduction.HypergeometricB,
            parameterC: reduction.HypergeometricC
        );
        var shiftedB = (
            reduction.HypergeometricB + RealQuadratic.Rational(value: reduction.EulerShift)
        );
        var shiftedCMinusB = (
            (reduction.HypergeometricC - reduction.HypergeometricB) +
            RealQuadratic.Rational(value: reduction.EulerShift)
        );
        var expectedRatioTarget = (
            (RealQuadratic.Rational(value: reduction.BetaConstant) / dominant) -
            ((reduction.Alpha * reduction.InitialZero) /
                (dominant * reduction.InitialMinusOne))
        );

        return
            (
            (reduction.InitialZero == expectedInitialZero) &&
            ((dominant + other) == p) &&
            ((dominant * other) == -r) &&
            (reduction.HypergeometricArgument == (other / dominant)) &&
            (reduction.HypergeometricRatioTarget == expectedRatioTarget) &&
            (reduction.HypergeometricArgument.Abs() < RealQuadratic.One) &&
            (reduction.HypergeometricA == expectedA) &&
            (reduction.HypergeometricA == decomposedA) &&
            (reduction.HypergeometricB == (reduction.Alpha - RealQuadratic.One)) &&
            (reduction.HypergeometricC ==
                (reduction.HypergeometricA + (reduction.GammaConstant / r))) &&
            (reduction.EulerShift == expectedEulerShift) &&
            (shiftedB.Sign > 0) &&
            (shiftedCMinusB.Sign > 0) &&
            (((r * (reduction.Alpha - RealQuadratic.One)) + reduction.GammaConstant) ==
                RealQuadratic.Rational(value: shiftedLinear)) &&
            ((reduction.GammaConstant * (reduction.Alpha - RealQuadratic.One)) ==
                RealQuadratic.Rational(value: shiftedConstant))
        );
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
        PolynomialTailIndex.RequirePositive(tailIndex: tailIndex);
        reduction = default;

        if (!TryFactoredDegreeOneParameters(
            alpha: out var alpha,
            gammaConstant: out var gammaConstant,
            tailIndex: tailIndex
        )) {
            return false;
        }
        if (!Slope.IsRational) { return false; }
        var baseAtTail = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var firstRoot = Slope;
        var secondRoot = (RealQuadratic.Rational(value: Parameters.Linear) - Slope);

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
            InitialZero: RealQuadratic.Rational(value: (baseAtTail - integerBoundary))
        );

        if (VerifyDegreeOneMinimalityReduction(reduction: reduction)) { return true; }
        reduction = default;
        return false;
    }
    /// <summary>Rechecks every rational coefficient and shifted polynomial identity in a minimality reduction.</summary>
    public bool VerifyDegreeOneMinimalityReduction(PolynomialTailMinimalityReduction reduction) {
        if (
            (reduction.TailIndex <= BigInteger.Zero) ||
            !reduction.Alpha.IsRational ||
            !reduction.GammaConstant.IsRational ||
            !reduction.FirstCharacteristicRoot.IsRational ||
            !reduction.SecondCharacteristicRoot.IsRational ||
            !reduction.InitialMinusOne.IsRational ||
            !reduction.InitialZero.IsRational ||
            (reduction.BetaSlope != Parameters.Linear) ||
            (reduction.GammaSlope != Parameters.NumeratorQuadratic) ||
            (reduction.BetaConstant != ((Parameters.Linear * reduction.TailIndex) + Parameters.Constant)) ||
            (reduction.FirstCharacteristicRoot == reduction.SecondCharacteristicRoot) ||
            (reduction.InitialMinusOne != reduction.Alpha)
        ) {
            return false;
        }

        var firstRoot = reduction.FirstCharacteristicRoot;
        var secondRoot = reduction.SecondCharacteristicRoot;

        if (
            ((firstRoot + secondRoot) != RealQuadratic.Rational(value: Parameters.Linear)) ||
            ((firstRoot * secondRoot) != -RealQuadratic.Rational(value: Parameters.NumeratorQuadratic))
        ) {
            return false;
        }

        var shift = (reduction.TailIndex - 1);
        var shiftedLinear = (((2 * Parameters.NumeratorQuadratic) * shift) + Parameters.NumeratorLinear);
        var shiftedConstant = (
            (((Parameters.NumeratorQuadratic * shift) * shift) +
            (Parameters.NumeratorLinear * shift)) +
            Parameters.NumeratorConstant
        );
        var expectedInitialZero = RealQuadratic.Rational(value: (((Parameters.Linear * reduction.TailIndex) + Parameters.Constant) - reduction.IntegerBoundary));

        return
            (
            (reduction.InitialZero == expectedInitialZero) &&
            (((RealQuadratic.Rational(value: reduction.GammaSlope) * (reduction.Alpha - RealQuadratic.One)) +
                reduction.GammaConstant) == RealQuadratic.Rational(value: shiftedLinear)) &&
            ((reduction.GammaConstant * (reduction.Alpha - RealQuadratic.One)) ==
                RealQuadratic.Rational(value: shiftedConstant))
        );
    }

    private bool TryFactoredDegreeOneParameters(
        BigInteger tailIndex,
        out RealQuadratic alpha,
        out RealQuadratic gammaConstant) {
        alpha = default;
        gammaConstant = default;
        var numeratorDiscriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            ((4 * Parameters.NumeratorQuadratic) * Parameters.NumeratorConstant)
        );

        if (numeratorDiscriminant < BigInteger.Zero) { return false; }
        var numeratorRoot = BigIntegerFunctions.SquareRoot(value: numeratorDiscriminant);

        if ((numeratorRoot * numeratorRoot) != numeratorDiscriminant) { return false; }

        var twoR = (2 * Parameters.NumeratorQuadratic);
        var firstOffset = RealQuadratic.Rational(
            denominator: twoR,
            numerator: (Parameters.NumeratorLinear + numeratorRoot)
        );
        var secondOffset = RealQuadratic.Rational(
            denominator: twoR,
            numerator: (Parameters.NumeratorLinear - numeratorRoot)
        );

        alpha = (RealQuadratic.Rational(value: tailIndex) + firstOffset);
        gammaConstant = (RealQuadratic.Rational(value: Parameters.NumeratorQuadratic) *
            (RealQuadratic.Rational(value: (tailIndex - 1)) + secondOffset));
        return true;
    }
    private static BigInteger EulerRegularizationShift(RealQuadratic parameterB, RealQuadratic parameterC) {
        var shift = BigInteger.Max(
            left: BigInteger.Zero,
            right: BigInteger.Max(
                left: ((-parameterB).Floor() + 1),
                right: ((parameterB - parameterC).Floor() + 1)
            )
        );

        return (shift.IsEven
            ? shift
            : (shift + 1)
        );
    }
}
