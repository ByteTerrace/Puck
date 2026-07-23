using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// An exact exclusion certificate for an integer tail value obtained from the positive Euler measure of the
/// associated Gauss hypergeometric quotient.
/// </summary>
/// <remarks>
/// When the quadratic numerator factors over the rationals, either orientation of its two roots gives parameters
/// <c>a,b,c</c> and <c>-1&lt;x&lt;0</c>.  If <c>b&gt;0</c> and <c>c-b&gt;0</c>, put
/// <c>w(t)=t^(b-1)*(1-t)^(c-b-1)*(1-x*t)^(-a)</c> on <c>(0,1)</c>.  A putative equality
/// <c>s_N=M</c> forces
/// <c>T=c*2F1(a,b;c;x)/2F1(a,b+1;c+1;x)=b/E[t]</c>, where <c>T=M/lambda</c>.
/// Integrating the derivative of <c>t^b*(1-t)^(c-b)*(1-x*t)^(-a)</c> gives
/// <c>T=c-a*x*E_t[(1-t)/(1-x*t)]</c>.  The last expectation is strictly between zero and one, so for
/// <c>a!=0</c> the target lies strictly between <c>c</c> and <c>c-a*x</c>; for <c>a=0</c> it equals <c>c</c>.
/// Also <c>T=b/E[t]&gt;b</c>.  A certificate records an exact violation of these necessary inequalities.
/// </remarks>
public readonly record struct PolynomialTailEulerMomentExclusionCertificate(
    BigInteger TailIndex,
    BigInteger IntegerBoundary,
    BigInteger SignedNumeratorDiscriminantRoot,
    QuadraticSurd HypergeometricA,
    QuadraticSurd HypergeometricB,
    QuadraticSurd HypergeometricC,
    QuadraticSurd HypergeometricArgument,
    QuadraticSurd HypergeometricRatioTarget,
    QuadraticSurd FirstEndpoint,
    QuadraticSurd SecondEndpoint
);

/// <summary>
/// An exact failed Hausdorff-moment inequality for a putative Euler representation of an integer tail equality.
/// </summary>
/// <param name="TailIndex">The positive recurrence index <c>n</c>.</param>
/// <param name="IntegerBoundary">The putative integer tail value <c>M</c> at that index.</param>
/// <param name="SignedNumeratorDiscriminantRoot">The exact square root of the quadratic numerator's discriminant; its sign selects which of the two rational roots orients the Euler chart.</param>
/// <param name="MomentIndex">The exponent <c>k</c> in <c>E[t^k*(1-t)^j]</c>.</param>
/// <param name="DifferenceOrder">The exponent <c>j</c> in <c>E[t^k*(1-t)^j]</c>.</param>
/// <param name="Witness">The exact nonpositive value of that putative moment.</param>
public readonly record struct PolynomialTailEulerHausdorffExclusionCertificate(
    BigInteger TailIndex,
    BigInteger IntegerBoundary,
    BigInteger SignedNumeratorDiscriminantRoot,
    int MomentIndex,
    int DifferenceOrder,
    QuadraticSurd Witness
);

/// <summary>
/// An exact failed Hausdorff inequality after moving a non-native Gauss chart into the positive Euler region by
/// contiguous parameter shifts.
/// </summary>
/// <remarks>
/// If <c>m_k</c> denotes the formal moment sequence forced by a proposed equality, then
/// <c>W(k,j)=sum_i (-1)^i*binomial(j,i)*m_(k+i)</c> is the contiguous Gauss function with endpoint exponents
/// <c>b+k</c> and <c>c-b+j</c>, up to a common nonzero factor.  At the canonical positive anchor
/// <c>(AnchorMomentIndex,AnchorDifferenceOrder)</c>, equality forces every later <c>W</c> to have the same strict
/// sign as the anchor.  A zero anchor or a nonpositive anchor/witness product is therefore an exact exclusion.
/// Resonant zero Pochhammer factors are deliberately not certified by this record.
/// </remarks>
public readonly record struct PolynomialTailEulerRegularizedHausdorffExclusionCertificate(
    BigInteger TailIndex,
    BigInteger IntegerBoundary,
    BigInteger GaussTailIndex,
    QuadraticSurd GaussBoundary,
    BigInteger SignedNumeratorDiscriminantRoot,
    int AnchorMomentIndex,
    int AnchorDifferenceOrder,
    int WitnessMomentIndex,
    int WitnessDifferenceOrder,
    QuadraticSurd Anchor,
    QuadraticSurd Witness
);

/// <summary>
/// The canonical nonresonant contiguous shifts that put a Gauss quotient into a positive Euler chart.
/// </summary>
/// <remarks>
/// Usually <c>GaussTailIndex=TailIndex</c> and <c>GaussBoundary=IntegerBoundary</c>.  In the sole double-zero
/// resonance <c>B_n=r*n^2</c> at <c>TailIndex=1</c>, the record instead carries the exactly equivalent Riccati-shifted
/// rational boundary <c>s_2=r/(IntegerBoundary-A_1)</c>.
/// </remarks>
public readonly record struct PolynomialTailEulerRegularization(
    BigInteger TailIndex,
    BigInteger IntegerBoundary,
    BigInteger GaussTailIndex,
    QuadraticSurd GaussBoundary,
    BigInteger SignedNumeratorDiscriminantRoot,
    QuadraticSurd HypergeometricA,
    QuadraticSurd HypergeometricB,
    QuadraticSurd HypergeometricC,
    QuadraticSurd HypergeometricArgument,
    QuadraticSurd HypergeometricRatioTarget,
    BigInteger MomentShift,
    BigInteger DifferenceShift
);

public sealed partial class PolynomialContinuedFractionAnalysis {
    /// <summary>The largest requested total moment order accepted by the dense exact Hausdorff search.</summary>
    public const int MaximumEulerHausdorffMomentOrder = 256;

    /// <summary>
    /// Attempts to prove that <c>s_tailIndex</c> is not <paramref name="integerBoundary"/> by an exact Euler-moment
    /// interval.  This applies to rationally factored numerators even when the characteristic roots and Gauss
    /// parameter <c>a</c> are genuinely quadratic, so it reaches beyond the 1-period reduction.
    /// </summary>
    public bool TryEulerMomentIntegerExclusionCertificate(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        out PolynomialTailEulerMomentExclusionCertificate certificate) {
        ValidateTailIndex(tailIndex);
        certificate = default;

        var discriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * Parameters.NumeratorQuadratic * Parameters.NumeratorConstant)
        );
        if (discriminant < BigInteger.Zero) { return false; }
        var root = BigIntegerFunctions.SquareRoot(discriminant);
        if ((root * root) != discriminant) { return false; }

        if (TryCreateEulerMomentIntegerExclusionCertificate(
            tailIndex,
            integerBoundary,
            root,
            out certificate)) {
            return true;
        }
        return !root.IsZero && TryCreateEulerMomentIntegerExclusionCertificate(
            tailIndex,
            integerBoundary,
            -root,
            out certificate
        );
    }

    /// <summary>Rechecks every algebraic identity and strict order condition in an Euler-moment exclusion.</summary>
    public bool VerifyEulerMomentIntegerExclusionCertificate(
        PolynomialTailEulerMomentExclusionCertificate certificate) {
        if (certificate.TailIndex <= BigInteger.Zero) { return false; }
        if (!TryCreateEulerMomentData(
            certificate.TailIndex,
            certificate.IntegerBoundary,
            certificate.SignedNumeratorDiscriminantRoot,
            out var expected)) {
            return false;
        }

        return (certificate == expected) && EulerMomentTargetIsExcluded(certificate);
    }

    /// <summary>
    /// Attempts to exclude an integer equality by generating its forced Euler moments and finding a failed Hausdorff
    /// inequality <c>E[t^k*(1-t)^j]&gt;0</c>.  Increasing <paramref name="maximumTotalOrder"/> gives a nested exact
    /// exclusion search; no numerical quadrature or guessed tolerance is used.
    /// </summary>
    public bool TryEulerHausdorffIntegerExclusionCertificate(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        int maximumTotalOrder,
        out PolynomialTailEulerHausdorffExclusionCertificate certificate) {
        ValidateTailIndex(tailIndex);
        if ((maximumTotalOrder < 1) || (maximumTotalOrder > MaximumEulerHausdorffMomentOrder)) {
            throw new ArgumentOutOfRangeException(nameof(maximumTotalOrder));
        }
        certificate = default;

        var discriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * Parameters.NumeratorQuadratic * Parameters.NumeratorConstant)
        );
        if (discriminant < BigInteger.Zero) { return false; }
        var root = BigIntegerFunctions.SquareRoot(discriminant);
        if ((root * root) != discriminant) { return false; }

        if (TryCreateEulerHausdorffIntegerExclusionCertificate(
            tailIndex,
            integerBoundary,
            root,
            maximumTotalOrder,
            out certificate)) {
            return true;
        }
        return !root.IsZero && TryCreateEulerHausdorffIntegerExclusionCertificate(
            tailIndex,
            integerBoundary,
            -root,
            maximumTotalOrder,
            out certificate
        );
    }

    /// <summary>Recomputes the forced moment recurrence and the recorded failed Hausdorff inequality.</summary>
    public bool VerifyEulerHausdorffIntegerExclusionCertificate(
        PolynomialTailEulerHausdorffExclusionCertificate certificate) {
        if ((certificate.TailIndex <= BigInteger.Zero) || (certificate.MomentIndex < 0) ||
            (certificate.DifferenceOrder < 0) ||
            ((certificate.MomentIndex + certificate.DifferenceOrder) < 1) ||
            ((certificate.MomentIndex + certificate.DifferenceOrder) > MaximumEulerHausdorffMomentOrder) ||
            !TryCreateEulerMomentData(
                certificate.TailIndex,
                certificate.IntegerBoundary,
                certificate.SignedNumeratorDiscriminantRoot,
                out var data) ||
            !TryForcedEulerMoments(
                data,
                certificate.MomentIndex + certificate.DifferenceOrder,
                out var moments)) {
            return false;
        }

        var witness = EulerHausdorffWitness(
            moments,
            certificate.MomentIndex,
            certificate.DifferenceOrder
        );
        return (certificate.Witness == witness) && (witness.Sign <= 0);
    }

    /// <summary>
    /// Attempts the Hausdorff exclusion after the least separate contiguous shifts that make both Euler endpoint
    /// exponents positive.  The requested order is measured beyond that anchor.  The dense exact computation is
    /// refused when the anchor plus the requested order exceeds <see cref="MaximumEulerHausdorffMomentOrder"/>.
    /// </summary>
    public bool TryEulerRegularizedHausdorffIntegerExclusionCertificate(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        int maximumAdditionalOrder,
        out PolynomialTailEulerRegularizedHausdorffExclusionCertificate certificate) {
        ValidateTailIndex(tailIndex);
        if ((maximumAdditionalOrder < 1) ||
            (maximumAdditionalOrder > MaximumEulerHausdorffMomentOrder)) {
            throw new ArgumentOutOfRangeException(nameof(maximumAdditionalOrder));
        }
        certificate = default;

        var discriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * Parameters.NumeratorQuadratic * Parameters.NumeratorConstant)
        );
        if (discriminant < BigInteger.Zero) { return false; }
        var root = BigIntegerFunctions.SquareRoot(discriminant);
        if ((root * root) != discriminant) { return false; }

        if (TryCreateEulerRegularizedHausdorffIntegerExclusionCertificate(
            tailIndex,
            integerBoundary,
            root,
            maximumAdditionalOrder,
            out certificate)) {
            return true;
        }
        return !root.IsZero && TryCreateEulerRegularizedHausdorffIntegerExclusionCertificate(
            tailIndex,
            integerBoundary,
            -root,
            maximumAdditionalOrder,
            out certificate
        );
    }

    /// <summary>Reconstructs the canonical positive chart and its failed sign-normalized Hausdorff inequality.</summary>
    public bool VerifyEulerRegularizedHausdorffIntegerExclusionCertificate(
        PolynomialTailEulerRegularizedHausdorffExclusionCertificate certificate) {
        if ((certificate.TailIndex <= BigInteger.Zero) ||
            (certificate.AnchorMomentIndex < 0) || (certificate.AnchorDifferenceOrder < 0) ||
            (certificate.WitnessMomentIndex < certificate.AnchorMomentIndex) ||
            (certificate.WitnessDifferenceOrder < certificate.AnchorDifferenceOrder) ||
            (certificate.WitnessMomentIndex > MaximumEulerHausdorffMomentOrder) ||
            (certificate.WitnessDifferenceOrder > MaximumEulerHausdorffMomentOrder) ||
            ((certificate.WitnessMomentIndex + certificate.WitnessDifferenceOrder) >
                MaximumEulerHausdorffMomentOrder) ||
            !TryCreateEulerRegularizedMomentData(
                certificate.TailIndex,
                certificate.IntegerBoundary,
                certificate.SignedNumeratorDiscriminantRoot,
                out var data,
                out var gaussTailIndex,
                out var gaussBoundary) ||
            (certificate.GaussTailIndex != gaussTailIndex) ||
            (certificate.GaussBoundary != gaussBoundary) ||
            !TryEulerRegularizationAnchor(
                data,
                out var expectedMomentIndex,
                out var expectedDifferenceOrder) ||
            (certificate.AnchorMomentIndex != expectedMomentIndex) ||
            (certificate.AnchorDifferenceOrder != expectedDifferenceOrder)) {
            return false;
        }

        var maximumMomentOrder = (
            certificate.WitnessMomentIndex + certificate.WitnessDifferenceOrder
        );
        if (!TryForcedEulerMoments(data, maximumMomentOrder, out var moments)) {
            return false;
        }

        var anchor = EulerHausdorffWitness(
            moments,
            certificate.AnchorMomentIndex,
            certificate.AnchorDifferenceOrder
        );
        var witness = EulerHausdorffWitness(
            moments,
            certificate.WitnessMomentIndex,
            certificate.WitnessDifferenceOrder
        );
        var isZeroAnchorCertificate =
            (certificate.WitnessMomentIndex == certificate.AnchorMomentIndex) &&
            (certificate.WitnessDifferenceOrder == certificate.AnchorDifferenceOrder) &&
            anchor == QuadraticSurd.Zero;
        var isFailedStrictSign =
            !isZeroAnchorCertificate &&
            ((certificate.WitnessMomentIndex + certificate.WitnessDifferenceOrder) >
                (certificate.AnchorMomentIndex + certificate.AnchorDifferenceOrder)) &&
            (anchor * witness).Sign <= 0;

        return (certificate.Anchor == anchor) && (certificate.Witness == witness) &&
            (isZeroAnchorCertificate || isFailedStrictSign);
    }

    /// <summary>
    /// Constructs the least separate nonnegative contiguous shifts <c>K,J</c> for which
    /// <c>b+K&gt;0</c> and <c>c-b+J&gt;0</c>.  Success also certifies that the Pochhammer prefactor connecting this
    /// positive Euler chart to the original quotient is nonzero.  Unlike the dense Hausdorff search, the shifts in
    /// this reduction are unbounded <see cref="BigInteger"/> values.  The sole double-zero resonance is first moved
    /// one Riccati step to its equivalent rational boundary.
    /// </summary>
    public bool TryEulerMomentRegularization(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        out PolynomialTailEulerRegularization regularization) {
        ValidateTailIndex(tailIndex);
        regularization = default;

        var discriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * Parameters.NumeratorQuadratic * Parameters.NumeratorConstant)
        );
        if (discriminant < BigInteger.Zero) { return false; }
        var root = BigIntegerFunctions.SquareRoot(discriminant);
        if ((root * root) != discriminant) { return false; }

        if (TryCreateEulerMomentRegularization(
            tailIndex,
            integerBoundary,
            root,
            out regularization)) {
            return true;
        }
        return !root.IsZero && TryCreateEulerMomentRegularization(
            tailIndex,
            integerBoundary,
            -root,
            out regularization
        );
    }

    /// <summary>Rechecks the Gauss parameters, canonical positivity shifts, and nonresonance conditions.</summary>
    public bool VerifyEulerMomentRegularization(PolynomialTailEulerRegularization regularization) {
        if ((regularization.TailIndex <= BigInteger.Zero) ||
            !TryCreateEulerRegularizedMomentData(
                regularization.TailIndex,
                regularization.IntegerBoundary,
                regularization.SignedNumeratorDiscriminantRoot,
                out var data,
                out var gaussTailIndex,
                out var gaussBoundary) ||
            !TryEulerRegularizationShifts(data, out var momentShift, out var differenceShift)) {
            return false;
        }

        return
            (regularization.HypergeometricA == data.HypergeometricA) &&
            (regularization.GaussTailIndex == gaussTailIndex) &&
            (regularization.GaussBoundary == gaussBoundary) &&
            (regularization.HypergeometricB == data.HypergeometricB) &&
            (regularization.HypergeometricC == data.HypergeometricC) &&
            (regularization.HypergeometricArgument == data.HypergeometricArgument) &&
            (regularization.HypergeometricRatioTarget == data.HypergeometricRatioTarget) &&
            (regularization.MomentShift == momentShift) &&
            (regularization.DifferenceShift == differenceShift);
    }

    private bool TryCreateEulerMomentIntegerExclusionCertificate(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        BigInteger signedRoot,
        out PolynomialTailEulerMomentExclusionCertificate certificate) {
        if (!TryCreateEulerMomentData(tailIndex, integerBoundary, signedRoot, out certificate)) {
            return false;
        }
        if (EulerMomentTargetIsExcluded(certificate) &&
            VerifyEulerMomentIntegerExclusionCertificate(certificate)) {
            return true;
        }

        certificate = default;
        return false;
    }

    private bool TryCreateEulerHausdorffIntegerExclusionCertificate(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        BigInteger signedRoot,
        int maximumTotalOrder,
        out PolynomialTailEulerHausdorffExclusionCertificate certificate) {
        certificate = default;
        if (!TryCreateEulerMomentData(tailIndex, integerBoundary, signedRoot, out var data) ||
            !TryForcedEulerMoments(data, maximumTotalOrder, out var moments)) {
            return false;
        }

        for (var totalOrder = 1; totalOrder <= maximumTotalOrder; ++totalOrder) {
            for (var momentIndex = 0; momentIndex <= totalOrder; ++momentIndex) {
                var differenceOrder = (totalOrder - momentIndex);
                var witness = EulerHausdorffWitness(moments, momentIndex, differenceOrder);
                if (witness.Sign > 0) { continue; }

                var candidate = new PolynomialTailEulerHausdorffExclusionCertificate(
                    TailIndex: tailIndex,
                    IntegerBoundary: integerBoundary,
                    SignedNumeratorDiscriminantRoot: signedRoot,
                    MomentIndex: momentIndex,
                    DifferenceOrder: differenceOrder,
                    Witness: witness
                );
                if (!VerifyEulerHausdorffIntegerExclusionCertificate(candidate)) { return false; }
                certificate = candidate;
                return true;
            }
        }
        return false;
    }

    private bool TryCreateEulerRegularizedHausdorffIntegerExclusionCertificate(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        BigInteger signedRoot,
        int maximumAdditionalOrder,
        out PolynomialTailEulerRegularizedHausdorffExclusionCertificate certificate) {
        certificate = default;
        if (!TryCreateEulerRegularizedMomentData(
                tailIndex,
                integerBoundary,
                signedRoot,
                out var data,
                out var gaussTailIndex,
                out var gaussBoundary) ||
            !TryEulerRegularizationAnchor(
                data,
                out var anchorMomentIndex,
                out var anchorDifferenceOrder)) {
            return false;
        }

        var anchorTotalOrder = checked(anchorMomentIndex + anchorDifferenceOrder);
        if (anchorTotalOrder > (MaximumEulerHausdorffMomentOrder - maximumAdditionalOrder) ||
            !TryForcedEulerMoments(
                data,
                anchorTotalOrder + maximumAdditionalOrder,
                out var moments)) {
            return false;
        }

        var anchor = EulerHausdorffWitness(moments, anchorMomentIndex, anchorDifferenceOrder);
        if (anchor == QuadraticSurd.Zero) {
            var zeroAnchor = new PolynomialTailEulerRegularizedHausdorffExclusionCertificate(
                TailIndex: tailIndex,
                IntegerBoundary: integerBoundary,
                GaussTailIndex: gaussTailIndex,
                GaussBoundary: gaussBoundary,
                SignedNumeratorDiscriminantRoot: signedRoot,
                AnchorMomentIndex: anchorMomentIndex,
                AnchorDifferenceOrder: anchorDifferenceOrder,
                WitnessMomentIndex: anchorMomentIndex,
                WitnessDifferenceOrder: anchorDifferenceOrder,
                Anchor: anchor,
                Witness: anchor
            );
            if (!VerifyEulerRegularizedHausdorffIntegerExclusionCertificate(zeroAnchor)) { return false; }
            certificate = zeroAnchor;
            return true;
        }

        for (var additionalOrder = 1; additionalOrder <= maximumAdditionalOrder; ++additionalOrder) {
            for (var momentIncrement = 0; momentIncrement <= additionalOrder; ++momentIncrement) {
                var witnessMomentIndex = checked(anchorMomentIndex + momentIncrement);
                var witnessDifferenceOrder = checked(
                    anchorDifferenceOrder + additionalOrder - momentIncrement
                );
                var witness = EulerHausdorffWitness(
                    moments,
                    witnessMomentIndex,
                    witnessDifferenceOrder
                );
                if ((anchor * witness).Sign > 0) { continue; }

                var candidate = new PolynomialTailEulerRegularizedHausdorffExclusionCertificate(
                TailIndex: tailIndex,
                IntegerBoundary: integerBoundary,
                GaussTailIndex: gaussTailIndex,
                GaussBoundary: gaussBoundary,
                    SignedNumeratorDiscriminantRoot: signedRoot,
                    AnchorMomentIndex: anchorMomentIndex,
                    AnchorDifferenceOrder: anchorDifferenceOrder,
                    WitnessMomentIndex: witnessMomentIndex,
                    WitnessDifferenceOrder: witnessDifferenceOrder,
                    Anchor: anchor,
                    Witness: witness
                );
                if (!VerifyEulerRegularizedHausdorffIntegerExclusionCertificate(candidate)) { return false; }
                certificate = candidate;
                return true;
            }
        }
        return false;
    }

    private bool TryCreateEulerMomentRegularization(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        BigInteger signedRoot,
        out PolynomialTailEulerRegularization regularization) {
        regularization = default;
        if (!TryCreateEulerRegularizedMomentData(
                tailIndex,
                integerBoundary,
                signedRoot,
                out var data,
                out var gaussTailIndex,
                out var gaussBoundary) ||
            !TryEulerRegularizationShifts(data, out var momentShift, out var differenceShift)) {
            return false;
        }

        var candidate = new PolynomialTailEulerRegularization(
            TailIndex: tailIndex,
            IntegerBoundary: integerBoundary,
            GaussTailIndex: gaussTailIndex,
            GaussBoundary: gaussBoundary,
            SignedNumeratorDiscriminantRoot: signedRoot,
            HypergeometricA: data.HypergeometricA,
            HypergeometricB: data.HypergeometricB,
            HypergeometricC: data.HypergeometricC,
            HypergeometricArgument: data.HypergeometricArgument,
            HypergeometricRatioTarget: data.HypergeometricRatioTarget,
            MomentShift: momentShift,
            DifferenceShift: differenceShift
        );
        if (!VerifyEulerMomentRegularization(candidate)) { return false; }
        regularization = candidate;
        return true;
    }

    private static bool TryEulerRegularizationAnchor(
        PolynomialTailEulerMomentExclusionCertificate data,
        out int momentIndex,
        out int differenceOrder) {
        momentIndex = 0;
        differenceOrder = 0;
        if (!TryEulerRegularizationShifts(data, out var momentShift, out var differenceShift) ||
            (momentShift > MaximumEulerHausdorffMomentOrder) ||
            (differenceShift > MaximumEulerHausdorffMomentOrder) ||
            ((momentShift + differenceShift) > MaximumEulerHausdorffMomentOrder)) {
            return false;
        }

        momentIndex = (int)momentShift;
        differenceOrder = (int)differenceShift;
        return true;
    }

    private static bool TryEulerRegularizationShifts(
        PolynomialTailEulerMomentExclusionCertificate data,
        out BigInteger momentShift,
        out BigInteger differenceShift) {
        momentShift = BigInteger.Max(
            BigInteger.Zero,
            (-data.HypergeometricB).Floor() + BigInteger.One
        );
        differenceShift = BigInteger.Max(
            BigInteger.Zero,
            (data.HypergeometricB - data.HypergeometricC).Floor() + BigInteger.One
        );
        if ((data.HypergeometricB + QuadraticSurd.Rational(momentShift)).Sign <= 0 ||
            (data.HypergeometricC - data.HypergeometricB +
                QuadraticSurd.Rational(differenceShift)).Sign <= 0) {
            return false;
        }

        // The finite-difference/contiguous identity has prefactor
        // (b)_momentIndex*(c-b)_differenceOrder/(c)_(momentIndex+differenceOrder).
        // A zero numerator factor is resonant and cannot be normalized into a positive moment measure by division.
        return
            PochhammerIsNonzero(data.HypergeometricB, momentShift) &&
            PochhammerIsNonzero(data.HypergeometricC - data.HypergeometricB, differenceShift) &&
            PochhammerIsNonzero(data.HypergeometricC, momentShift + differenceShift);
    }

    private static bool PochhammerIsNonzero(QuadraticSurd initial, BigInteger length) {
        if ((length <= BigInteger.Zero) || !initial.IsRational) { return true; }
        var numerator = initial.RationalNumerator;
        var denominator = initial.Denominator;
        if ((numerator % denominator) != BigInteger.Zero) { return true; }
        var integer = (numerator / denominator);
        return (integer > BigInteger.Zero) || (-integer >= length);
    }

    private bool TryCreateEulerRegularizedMomentData(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        BigInteger signedRoot,
        out PolynomialTailEulerMomentExclusionCertificate data,
        out BigInteger gaussTailIndex,
        out QuadraticSurd gaussBoundary) {
        gaussTailIndex = tailIndex;
        gaussBoundary = QuadraticSurd.Rational(integerBoundary);
        if (TryCreateEulerMomentData(
                tailIndex,
                gaussBoundary,
                integerBoundary,
                signedRoot,
                requirePositiveEulerChart: false,
                out data) &&
            TryEulerRegularizationShifts(data, out _, out _)) {
            return true;
        }

        // Positivity of B_n rules out every zero-Pochhammer resonance except N=1 and B_n=r*n^2.  If a proposed
        // positive tail hit is M=A_1+d, then d must be positive and the Riccati equation is exactly equivalent to
        // s_2=B_1/d=r/d.  At N=2 the formerly double-zero factor has b=1, so the quotient chart is nonresonant.
        data = default;
        if ((tailIndex != BigInteger.One) || !signedRoot.IsZero ||
            !Parameters.NumeratorLinear.IsZero || !Parameters.NumeratorConstant.IsZero) {
            return false;
        }
        var baseAtOne = (Parameters.Linear + Parameters.Constant);
        var boundaryIncrement = (integerBoundary - baseAtOne);
        if (boundaryIncrement <= BigInteger.Zero) { return false; }

        gaussTailIndex = (tailIndex + BigInteger.One);
        gaussBoundary = QuadraticSurd.Rational(
            Parameters.NumeratorQuadratic,
            boundaryIncrement
        );
        return
            TryCreateEulerMomentData(
                gaussTailIndex,
                gaussBoundary,
                integerBoundary,
                signedRoot,
                requirePositiveEulerChart: false,
                out data) &&
            TryEulerRegularizationShifts(data, out _, out _);
    }

    private bool TryCreateEulerMomentData(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        BigInteger signedRoot,
        out PolynomialTailEulerMomentExclusionCertificate certificate) {
        return TryCreateEulerMomentData(
            tailIndex,
            QuadraticSurd.Rational(integerBoundary),
            integerBoundary,
            signedRoot,
            requirePositiveEulerChart: true,
            out certificate
        );
    }

    private bool TryCreateEulerMomentData(
        BigInteger tailIndex,
        BigInteger integerBoundary,
        BigInteger signedRoot,
        bool requirePositiveEulerChart,
        out PolynomialTailEulerMomentExclusionCertificate certificate) {
        return TryCreateEulerMomentData(
            tailIndex,
            QuadraticSurd.Rational(integerBoundary),
            integerBoundary,
            signedRoot,
            requirePositiveEulerChart,
            out certificate
        );
    }

    private bool TryCreateEulerMomentData(
        BigInteger tailIndex,
        QuadraticSurd tailBoundary,
        BigInteger recordedIntegerBoundary,
        BigInteger signedRoot,
        bool requirePositiveEulerChart,
        out PolynomialTailEulerMomentExclusionCertificate certificate) {
        certificate = default;
        var rInteger = Parameters.NumeratorQuadratic;
        if (rInteger <= BigInteger.Zero) { return false; }
        var discriminant = (
            (Parameters.NumeratorLinear * Parameters.NumeratorLinear) -
            (4 * rInteger * Parameters.NumeratorConstant)
        );
        if ((signedRoot * signedRoot) != discriminant) { return false; }

        var p = QuadraticSurd.Rational(Parameters.Linear);
        var r = QuadraticSurd.Rational(rInteger);
        var dominant = Slope;
        var other = (p - dominant);
        var twoR = (2 * rInteger);
        var alpha = (
            QuadraticSurd.Rational(tailIndex) +
            QuadraticSurd.Rational(Parameters.NumeratorLinear + signedRoot, twoR)
        );
        var gammaConstant = r * (
            QuadraticSurd.Rational(tailIndex - 1) +
            QuadraticSurd.Rational(Parameters.NumeratorLinear - signedRoot, twoR)
        );
        var betaConstant = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var parameterA = (
            ((other * other * alpha) -
                (other * QuadraticSurd.Rational(betaConstant)) - gammaConstant) /
            (other * (other - dominant))
        );
        var parameterB = (alpha - QuadraticSurd.One);
        var parameterC = (parameterA + (gammaConstant / r));
        var argument = (other / dominant);
        var target = (tailBoundary / dominant);
        var secondEndpoint = (parameterC - (parameterA * argument));

        if ((argument.Sign >= 0) || (argument.Abs() >= QuadraticSurd.One) ||
            (requirePositiveEulerChart &&
                ((parameterB.Sign <= 0) || ((parameterC - parameterB).Sign <= 0)))) {
            return false;
        }

        certificate = new PolynomialTailEulerMomentExclusionCertificate(
            TailIndex: tailIndex,
            IntegerBoundary: recordedIntegerBoundary,
            SignedNumeratorDiscriminantRoot: signedRoot,
            HypergeometricA: parameterA,
            HypergeometricB: parameterB,
            HypergeometricC: parameterC,
            HypergeometricArgument: argument,
            HypergeometricRatioTarget: target,
            FirstEndpoint: parameterC,
            SecondEndpoint: secondEndpoint
        );
        return true;
    }

    private static bool EulerMomentTargetIsExcluded(
        PolynomialTailEulerMomentExclusionCertificate certificate) {
        var target = certificate.HypergeometricRatioTarget;
        if (target <= certificate.HypergeometricB) { return true; }
        var first = certificate.FirstEndpoint;
        var second = certificate.SecondEndpoint;
        if (first == second) { return target != first; }
        var lower = (first < second) ? first : second;
        var upper = (first > second) ? first : second;
        return (target <= lower) || (target >= upper);
    }

    private static bool TryForcedEulerMoments(
        PolynomialTailEulerMomentExclusionCertificate data,
        int maximumOrder,
        out QuadraticSurd[] moments) {
        moments = [];
        var target = data.HypergeometricRatioTarget;
        if (target == QuadraticSurd.Zero) { return false; }

        var result = new QuadraticSurd[maximumOrder + 1];
        result[0] = QuadraticSurd.One;
        if (maximumOrder >= 1) {
            result[1] = (data.HypergeometricB / target);
        }
        for (var index = 0; (index + 2) <= maximumOrder; ++index) {
            var k = QuadraticSurd.Rational(index);
            var denominator = data.HypergeometricArgument *
                (data.HypergeometricC + k + QuadraticSurd.One - data.HypergeometricA);
            if (denominator == QuadraticSurd.Zero) { return false; }
            var middleCoefficient = (
                data.HypergeometricC + k +
                (data.HypergeometricArgument *
                    (data.HypergeometricB + k + QuadraticSurd.One - data.HypergeometricA))
            );
            result[index + 2] = (
                (middleCoefficient * result[index + 1]) -
                ((data.HypergeometricB + k) * result[index])
            ) / denominator;
        }

        moments = result;
        return true;
    }

    private static QuadraticSurd EulerHausdorffWitness(
        IReadOnlyList<QuadraticSurd> moments,
        int momentIndex,
        int differenceOrder) {
        var result = QuadraticSurd.Zero;
        for (var offset = 0; offset <= differenceOrder; ++offset) {
            var sign = ((offset & 1) == 0) ? BigInteger.One : -BigInteger.One;
            result += QuadraticSurd.Rational(
                sign * BinomialCoefficient(differenceOrder, offset)
            ) * moments[momentIndex + offset];
        }
        return result;
    }
}
