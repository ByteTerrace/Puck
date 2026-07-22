using System.Numerics;

namespace Puck.Maths;

/// <summary>Identifies structurally useful subfamilies of an exact Beatty-trap certificate.</summary>
[Flags]
public enum PolynomialExactBeattyTrapFamily {
    /// <summary>No exact norm-gap trap was certified.</summary>
    None = 0,
    /// <summary>The general finite norm-gap criterion was verified.</summary>
    NormGap = 1,
    /// <summary><c>r=1</c> and <c>-p&lt;=q&lt;=0</c>.</summary>
    UnitQuadraticOffsetStrip = 2,
    /// <summary><c>q=-p</c> and <c>1&lt;=r&lt;=p</c>.</summary>
    ScaledNumeratorWedge = 4,
}

/// <summary>The integral quadratic-norm data associated with one integer boundary.</summary>
/// <param name="TailIndex">The positive recurrence index <c>n</c>.</param>
/// <param name="Boundary">The integer boundary <c>m</c>.</param>
/// <param name="SlopeCoefficient"><c>Q=K*n+p*q-2*r</c>.</param>
/// <param name="RationalCoefficient"><c>T=K*m-r*(p+2*q)</c>.</param>
/// <param name="NormQuotient">
/// The integer <c>F</c> satisfying <c>T^2-p*T*Q-r*Q^2=K*F</c>.
/// </param>
public readonly record struct PolynomialExactBeattyNormWitness(
    BigInteger TailIndex,
    BigInteger Boundary,
    BigInteger SlopeCoefficient,
    BigInteger RationalCoefficient,
    BigInteger NormQuotient
);

/// <summary>
/// A finite exact proof certificate for all-index Beatty equality in a quadratic-numerator polynomial tail.
/// </summary>
/// <remarks>
/// The covered recurrence is
/// <c>s_n=p*n+q+r*n^2/s_(n+1)</c>, with <c>p,r&gt;=1</c>, <c>-p&lt;=q&lt;=0</c>.
/// The certificate verifies a strict right-hand trap
/// <c>x_n&lt;s_n&lt;x_n+C/n</c>, where <c>x_n=Slope*n+Offset</c>, and proves that every
/// integer above <c>x_n</c> is farther away than <c>C/n</c> by an integral quadratic norm.
/// Consequently every positive solution has floor <c>floor(x_n)</c> for every <c>n&gt;=1</c>.
/// </remarks>
public readonly record struct PolynomialExactBeattyTrapCertificate(
    PolynomialExactBeattyTrapFamily Family,
    PolynomialContinuedFractionParameters Parameters,
    QuadraticSurd Slope,
    QuadraticSurd Offset,
    QuadraticSurd TrapWidth,
    BigInteger CharacteristicDiscriminant,
    BigInteger NormResidue,
    BigInteger MinimumPositiveNorm,
    BigInteger SuccessorDeficit,
    BigInteger MinimumSlopeCoefficient,
    QuadraticSurd NormImageBound,
    QuadraticSurd ContractionFactor
) {
    /// <summary>Returns the exact affine center <c>x_n</c>.</summary>
    public QuadraticSurd Center(BigInteger tailIndex) {
        ValidateTailIndex(tailIndex: tailIndex);
        return ((Slope * QuadraticSurd.Rational(value: tailIndex)) + Offset);
    }

    /// <summary>Returns the strict trapping interval endpoints <c>(x_n,x_n+C/n)</c>.</summary>
    public (QuadraticSurd Lower, QuadraticSurd Upper) Trap(BigInteger tailIndex) {
        var lower = Center(tailIndex: tailIndex);
        var width = (TrapWidth / QuadraticSurd.Rational(value: tailIndex));
        return (lower, lower + width);
    }

    /// <summary>Returns the certified floor of the unique positive tail at <paramref name="tailIndex"/>.</summary>
    public BigInteger TailFloor(BigInteger tailIndex) => Center(tailIndex: tailIndex).Floor();

    /// <summary>Constructs the exact integral norm witness for an integer boundary.</summary>
    public PolynomialExactBeattyNormWitness NormWitness(BigInteger tailIndex, BigInteger boundary) {
        ValidateTailIndex(tailIndex: tailIndex);

        var p = Parameters.Linear;
        var q = Parameters.Constant;
        var r = Parameters.NumeratorQuadratic;
        var modulus = CharacteristicDiscriminant;
        var slopeCoefficient = ((modulus * tailIndex) + (p * q) - (2 * r));
        var rationalCoefficient = ((modulus * boundary) - (r * (p + (2 * q))));
        var normQuotient = (
            modulus * (
                (boundary * boundary) - (p * boundary * tailIndex) -
                (r * tailIndex * tailIndex) - (q * boundary) + (r * tailIndex)
            )
        ) + NormResidue;

        return new PolynomialExactBeattyNormWitness(
            TailIndex: tailIndex,
            Boundary: boundary,
            SlopeCoefficient: slopeCoefficient,
            RationalCoefficient: rationalCoefficient,
            NormQuotient: normQuotient
        );
    }

    private static void ValidateTailIndex(BigInteger tailIndex) {
        if (tailIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index must be positive"
            );
        }
    }
}

/// <summary>Constructs and independently verifies exact quadratic Beatty-trap certificates without search.</summary>
public static class PolynomialExactBeattyTrap {
    /// <summary>Tries to construct a certificate directly from five recurrence coefficients.</summary>
    public static bool TryCreate(
        BigInteger linear,
        BigInteger constant,
        BigInteger numeratorQuadratic,
        BigInteger numeratorLinear,
        BigInteger numeratorConstant,
        out PolynomialExactBeattyTrapCertificate certificate) =>
        TryCreate(
            parameters: new PolynomialContinuedFractionParameters(
                Linear: linear,
                Constant: constant,
                NumeratorQuadratic: numeratorQuadratic,
                NumeratorLinear: numeratorLinear,
                NumeratorConstant: numeratorConstant
            ),
            certificate: out certificate
        );

    /// <summary>Tries to construct a certificate directly from recurrence parameters.</summary>
    public static bool TryCreate(
        PolynomialContinuedFractionParameters parameters,
        out PolynomialExactBeattyTrapCertificate certificate) {
        certificate = default;

        var p = parameters.Linear;
        var q = parameters.Constant;
        var r = parameters.NumeratorQuadratic;
        if ((parameters.NumeratorLinear != BigInteger.Zero) ||
            (parameters.NumeratorConstant != BigInteger.Zero) ||
            (p < BigInteger.One) || (r < BigInteger.One) ||
            (q < -p) || (q > BigInteger.Zero)) {
            return false;
        }

        var discriminant = ((p * p) + (4 * r));
        var slope = QuadraticSurd.Create(
            rationalNumerator: p,
            surdNumerator: BigInteger.One,
            radicand: discriminant,
            denominator: 2
        );
        var pSurd = QuadraticSurd.Rational(value: p);
        var qSurd = QuadraticSurd.Rational(value: q);
        var rSurd = QuadraticSurd.Rational(value: r);
        var slopeSquared = (slope * slope);
        var offset = (((qSurd * slopeSquared) - (rSurd * slope)) / (slopeSquared + rSurd));
        var discriminantRoot = ((QuadraticSurd.Rational(value: 2) * slope) - pSurd);
        var c = (slope + offset);
        var trapWidth = (rSurd * c * c / (slope * slope * slope));
        var normResidue = (r * ((q * (p + q)) - r));
        var minimumPositiveNorm = (discriminant + normResidue);
        var successorDeficit = ((2 * r) - (p * q));
        var minimumSlopeCoefficient = (discriminant - successorDeficit);
        var normImageBound = (
            QuadraticSurd.Rational(value: discriminant) * discriminantRoot * trapWidth
        );
        var contractionFactor = (rSurd / (pSurd * slope));
        var family = PolynomialExactBeattyTrapFamily.NormGap;
        if (r == BigInteger.One) {
            family |= PolynomialExactBeattyTrapFamily.UnitQuadraticOffsetStrip;
        }
        if ((q == -p) && (r <= p)) {
            family |= PolynomialExactBeattyTrapFamily.ScaledNumeratorWedge;
        }

        var candidate = new PolynomialExactBeattyTrapCertificate(
            Family: family,
            Parameters: parameters,
            Slope: slope,
            Offset: offset,
            TrapWidth: trapWidth,
            CharacteristicDiscriminant: discriminant,
            NormResidue: normResidue,
            MinimumPositiveNorm: minimumPositiveNorm,
            SuccessorDeficit: successorDeficit,
            MinimumSlopeCoefficient: minimumSlopeCoefficient,
            NormImageBound: normImageBound,
            ContractionFactor: contractionFactor
        );
        if (!Verify(certificate: candidate)) { return false; }

        certificate = candidate;
        return true;
    }

    /// <summary>Rechecks every exact algebraic and order obligation without constructing a tail analysis.</summary>
    public static bool Verify(PolynomialExactBeattyTrapCertificate certificate) {
        var parameters = certificate.Parameters;
        var p = parameters.Linear;
        var q = parameters.Constant;
        var r = parameters.NumeratorQuadratic;
        if ((parameters.NumeratorLinear != BigInteger.Zero) ||
            (parameters.NumeratorConstant != BigInteger.Zero) ||
            (p < BigInteger.One) || (r < BigInteger.One) ||
            (q < -p) || (q > BigInteger.Zero)) {
            return false;
        }

        var expectedFamily = PolynomialExactBeattyTrapFamily.NormGap;
        if (r == BigInteger.One) {
            expectedFamily |= PolynomialExactBeattyTrapFamily.UnitQuadraticOffsetStrip;
        }
        if ((q == -p) && (r <= p)) {
            expectedFamily |= PolynomialExactBeattyTrapFamily.ScaledNumeratorWedge;
        }
        if (certificate.Family != expectedFamily) { return false; }

        var pSurd = QuadraticSurd.Rational(value: p);
        var qSurd = QuadraticSurd.Rational(value: q);
        var rSurd = QuadraticSurd.Rational(value: r);
        var discriminant = ((p * p) + (4 * r));
        var expectedSlope = QuadraticSurd.Create(p, BigInteger.One, discriminant, 2);
        var discriminantRoot = ((QuadraticSurd.Rational(value: 2) * expectedSlope) - pSurd);
        var slopeSquared = (expectedSlope * expectedSlope);
        var expectedOffset = (
            (qSurd * slopeSquared) - (rSurd * expectedSlope)
        ) / (slopeSquared + rSurd);
        var c = (expectedSlope + expectedOffset);
        var expectedResidual = (rSurd * c * c / slopeSquared);
        var expectedTrapWidth = (expectedResidual / expectedSlope);
        var normResidue = (r * ((q * (p + q)) - r));
        var minimumPositiveNorm = (discriminant + normResidue);
        var successorDeficit = ((2 * r) - (p * q));
        var minimumSlopeCoefficient = (discriminant - successorDeficit);
        var expectedNormImageBound = (
            QuadraticSurd.Rational(value: discriminant) *
            discriminantRoot * expectedTrapWidth
        );
        var expectedContractionFactor = (rSurd / (pSurd * expectedSlope));

        if ((certificate.Slope != expectedSlope) ||
            (certificate.Offset != expectedOffset) ||
            (certificate.CharacteristicDiscriminant != discriminant) ||
            (certificate.NormResidue != normResidue) ||
            (certificate.MinimumPositiveNorm != minimumPositiveNorm) ||
            (certificate.SuccessorDeficit != successorDeficit) ||
            (certificate.MinimumSlopeCoefficient != minimumSlopeCoefficient) ||
            (certificate.TrapWidth != expectedTrapWidth) ||
            (certificate.NormImageBound != expectedNormImageBound) ||
            (certificate.ContractionFactor != expectedContractionFactor)) {
            return false;
        }

        if ((slopeSquared != ((pSurd * expectedSlope) + rSurd)) ||
            ((c * (slopeSquared + rSurd)) != (slopeSquared * (expectedSlope + qSurd))) ||
            ((qSurd - expectedOffset) != (rSurd * c / slopeSquared)) ||
            (expectedSlope <= pSurd) || (discriminantRoot.Sign <= 0) ||
            (c.Sign <= 0) || (c >= expectedSlope) ||
            (expectedTrapWidth.Sign <= 0) ||
            (normResidue >= BigInteger.Zero) ||
            (minimumPositiveNorm <= BigInteger.Zero) ||
            (successorDeficit <= BigInteger.Zero) ||
            (minimumSlopeCoefficient <= BigInteger.Zero) ||
            ((discriminantRoot * expectedTrapWidth) >=
                QuadraticSurd.Rational(value: successorDeficit)) ||
            (expectedNormImageBound >= QuadraticSurd.Rational(value: minimumPositiveNorm)) ||
            (expectedContractionFactor.Sign <= 0) ||
            (expectedContractionFactor >= QuadraticSurd.One) ||
            ((pSurd * slopeSquared).Sign <= 0) ||
            (((expectedSlope * slopeSquared) + (rSurd * c)).Sign <= 0)) {
            return false;
        }

        return VerifyNormPolynomialIdentity(
            p: p,
            q: q,
            r: r,
            modulus: discriminant,
            residue: normResidue
        );
    }

    private static bool VerifyNormPolynomialIdentity(
        BigInteger p,
        BigInteger q,
        BigInteger r,
        BigInteger modulus,
        BigInteger residue) {
        // Q=K*n+(p*q-2*r), T=K*m-r*(p+2*q), and
        // F=K*(m^2-p*m*n-r*n^2-q*m+r*n)+residue.
        // Compare coefficients of 1,n,m,n^2,n*m,m^2 in T^2-p*T*Q-r*Q^2 and K*F.
        var q0 = ((p * q) - (2 * r));
        var t0 = -(r * (p + (2 * q)));
        var left = new BigInteger[] {
            (t0 * t0) - (p * t0 * q0) - (r * q0 * q0),
            -(p * t0 * modulus) - (2 * r * q0 * modulus),
            (2 * t0 * modulus) - (p * modulus * q0),
            -(r * modulus * modulus),
            -(p * modulus * modulus),
            (modulus * modulus),
        };
        var right = new BigInteger[] {
            (modulus * residue),
            (modulus * modulus * r),
            -(modulus * modulus * q),
            -(modulus * modulus * r),
            -(modulus * modulus * p),
            (modulus * modulus),
        };

        return left.AsSpan().SequenceEqual(right);
    }
}

public sealed partial class PolynomialContinuedFractionAnalysis {
    /// <summary>Tries to certify exact Beatty equality for this already-constructed analysis.</summary>
    public bool TryExactBeattyTrapCertificate(out PolynomialExactBeattyTrapCertificate certificate) {
        if (!PolynomialExactBeattyTrap.TryCreate(parameters: Parameters, certificate: out certificate)) {
            return false;
        }
        if (VerifyExactBeattyTrapCertificate(certificate: certificate)) { return true; }

        certificate = default;
        return false;
    }

    /// <summary>Rechecks a standalone exact Beatty certificate against this analysis.</summary>
    public bool VerifyExactBeattyTrapCertificate(PolynomialExactBeattyTrapCertificate certificate) {
        if ((certificate.Parameters != Parameters) ||
            (certificate.Slope != Slope) ||
            (certificate.Offset != Offset) ||
            !PolynomialExactBeattyTrap.Verify(certificate: certificate)) {
            return false;
        }

        var c = (Slope + Offset);
        var r = QuadraticSurd.Rational(value: Parameters.NumeratorQuadratic);
        return (AffineResidual == (r * c * c / (Slope * Slope)));
    }
}
