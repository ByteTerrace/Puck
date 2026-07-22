using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A finite parameter certificate for the paired-forcing family whose proposed integer orbit must eventually change
/// sign.
/// </summary>
public readonly record struct PolynomialTailPairedForcingExclusionCertificate(
    BigInteger LinearScale,
    BigInteger FactorialScale,
    BigInteger Shift,
    BigInteger PositiveForcing,
    BigInteger IntegerBoundary
);

public sealed partial class PolynomialContinuedFractionAnalysis {
    /// <summary>
    /// Attempts to recognize the four-parameter family
    /// <c>p=P</c>, <c>q=P*(k-1)</c>, <c>r=c*(c+P)</c>,
    /// <c>u=r*(2*k-1)</c>, <c>v=r*k*(k-1)+h</c>, with <c>P,c,k,h&gt;=1</c>, at the proposed integer
    /// <c>s_1=k*(P+c)</c>.  Its cleared orbit is a positive factorial solution perturbed by summable paired forcing;
    /// the perturbation forces an eventual sign change, so the proposed integer is not the positive infinite tail.
    /// </summary>
    public bool TryPairedForcingIntegerExclusionCertificate(
        BigInteger integerBoundary,
        out PolynomialTailPairedForcingExclusionCertificate certificate) {
        certificate = default;
        var p = Parameters.Linear;
        var q = Parameters.Constant;
        var r = Parameters.NumeratorQuadratic;
        if ((p < BigInteger.One) || (r < BigInteger.One)) { return false; }

        var characteristicDiscriminant = ((p * p) + (4 * r));
        var characteristicRoot = BigIntegerMath.SquareRoot(characteristicDiscriminant);
        if ((characteristicRoot * characteristicRoot) != characteristicDiscriminant) { return false; }
        var cNumerator = (characteristicRoot - p);
        var c = BigInteger.DivRem(cNumerator, 2, out var cRemainder);
        if (!cRemainder.IsZero || (c < BigInteger.One) || (r != (c * (c + p)))) { return false; }

        var shiftMinusOne = BigInteger.DivRem(q, p, out var shiftRemainder);
        var k = (shiftMinusOne + 1);
        if (!shiftRemainder.IsZero || (k < BigInteger.One)) { return false; }
        var h = (Parameters.NumeratorConstant - (r * k * (k - 1)));
        var candidate = new PolynomialTailPairedForcingExclusionCertificate(
            LinearScale: p,
            FactorialScale: c,
            Shift: k,
            PositiveForcing: h,
            IntegerBoundary: integerBoundary
        );
        if (!VerifyPairedForcingIntegerExclusionCertificate(candidate)) { return false; }
        certificate = candidate;
        return true;
    }

    /// <summary>Rechecks the complete parameter identity of a paired-forcing exclusion certificate.</summary>
    public bool VerifyPairedForcingIntegerExclusionCertificate(
        PolynomialTailPairedForcingExclusionCertificate certificate) {
        var p = certificate.LinearScale;
        var c = certificate.FactorialScale;
        var k = certificate.Shift;
        var h = certificate.PositiveForcing;
        if ((p < BigInteger.One) || (c < BigInteger.One) || (k < BigInteger.One) || (h < BigInteger.One)) {
            return false;
        }
        var r = (c * (c + p));
        return
            (Parameters.Linear == p) &&
            (Parameters.Constant == (p * (k - 1))) &&
            (Parameters.NumeratorQuadratic == r) &&
            (Parameters.NumeratorLinear == (r * ((2 * k) - 1))) &&
            (Parameters.NumeratorConstant == ((r * k * (k - 1)) + h)) &&
            (certificate.IntegerBoundary == (k * (p + c)));
    }
}
