using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // The largest finite magnitude and the point above it from which round-to-nearest-even overflows: MaxValue is
    // (2^53 − 1)·2^971 and the next representable step would be 2^1024, so the midpoint is (2^54 − 1)·2^970 — a
    // tie there rounds to the even side, which is the overflow.
    private static readonly BigInteger OverflowMidpointNumerator = (((BigInteger.One << 54) - BigInteger.One) << 970);

    // Whether candidate is the double nearest numerator/denominator under round-to-nearest-even, decided with exact
    // integer arithmetic only: the candidate and its two binary64 neighbours are decomposed into mantissa·2^exponent,
    // everything is brought to one common power-of-two denominator, and the candidate wins when its distance is no
    // larger than either neighbour's — strictly smaller when its mantissa is odd, because a tie must go to the even
    // side. Infinity and zero are decided against the exact overflow and underflow midpoints.
    public static bool IsNearestDouble(BigInteger numerator, BigInteger denominator, double candidate) {
        if (denominator.Sign < 0) {
            numerator = -numerator;
            denominator = -denominator;
        }

        if (double.IsNaN(d: candidate)) { return false; }

        if (double.IsInfinity(d: candidate)) {
            // |value| ≥ (2^54 − 1)·2^970, sign matching.
            return ((double.IsPositiveInfinity(d: candidate) == (numerator.Sign > 0)) &&
                (BigInteger.Abs(value: numerator) >= (OverflowMidpointNumerator * denominator)));
        }

        if (candidate == 0.0) {
            // |value| ≤ 2^−1075: the tie at the underflow midpoint rounds to the even side, which is zero.
            return ((BigInteger.Abs(value: numerator) << 1075) <= denominator);
        }

        if ((candidate < 0.0) != (numerator.Sign < 0)) { return false; }

        var lower = Math.BitDecrement(x: candidate);
        var upper = Math.BitIncrement(x: candidate);

        if (double.IsInfinity(d: lower) || double.IsInfinity(d: upper)) {
            // Beside the overflow midpoint the finite side must simply be closer than infinity.
            return (BigInteger.Abs(value: numerator) < (OverflowMidpointNumerator * denominator));
        }

        var (candidateMantissa, candidateExponent) = Decompose(value: candidate);
        var (lowerMantissa, lowerExponent) = Decompose(value: lower);
        var (upperMantissa, upperExponent) = Decompose(value: upper);
        var commonExponent = Math.Min(val1: candidateExponent, val2: Math.Min(val1: lowerExponent, val2: upperExponent));
        var shift = Math.Max(val1: 0, val2: -commonExponent);
        var scaledValue = (numerator << shift);
        var candidateDistance = BigInteger.Abs(value: (scaledValue - (Scale(mantissa: candidateMantissa, exponent: (candidateExponent + shift)) * denominator)));
        var lowerDistance = BigInteger.Abs(value: (scaledValue - (Scale(mantissa: lowerMantissa, exponent: (lowerExponent + shift)) * denominator)));
        var upperDistance = BigInteger.Abs(value: (scaledValue - (Scale(mantissa: upperMantissa, exponent: (upperExponent + shift)) * denominator)));
        var mantissaIsOdd = !((BitConverter.DoubleToInt64Bits(value: candidate) & 1L) == 0L);

        return (mantissaIsOdd
            ? ((candidateDistance < lowerDistance) && (candidateDistance < upperDistance))
            : ((candidateDistance <= lowerDistance) && (candidateDistance <= upperDistance)));
    }

    // Exact integer scaling of a decomposed double, already brought to a non-negative exponent.
    private static BigInteger Scale(BigInteger mantissa, int exponent) => (mantissa << exponent);
    // value = mantissa · 2^exponent exactly, for a finite nonzero double, with the sign on the mantissa.
    private static (BigInteger Mantissa, int Exponent) Decompose(double value) {
        var bits = BitConverter.DoubleToInt64Bits(value: value);
        var biasedExponent = (int)((bits >> 52) & 0x7FFL);
        var fraction = (bits & 0xFFFFFFFFFFFFFL);
        var mantissa = ((biasedExponent == 0) ? fraction : (fraction | (1L << 52)));
        var exponent = ((biasedExponent == 0) ? -1074 : (biasedExponent - 1075));

        return (((bits < 0L) ? -new BigInteger(value: mantissa) : new BigInteger(value: mantissa)), exponent);
    }
}
