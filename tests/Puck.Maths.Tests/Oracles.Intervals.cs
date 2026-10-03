using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The reference hull of two exact raw endpoints: themselves when both lie in the carrier, and
    /// <see langword="null"/> (the unbounded interval) when either leaves it.</summary>
    /// <param name="lower">The exact lower endpoint, in raws.</param>
    /// <param name="upper">The exact upper endpoint, in raws.</param>
    /// <returns>The endpoints, or <see langword="null"/>.</returns>
    public static (long Lower, long Upper)? ExactHull(BigInteger lower, BigInteger upper) =>
        (((lower < long.MinValue) || (upper > long.MaxValue))
            ? null
            : (((long)lower), ((long)upper)));
    /// <summary>The reference directed hull of two exact rationals over one positive denominator: the floor of the
    /// lower and the ceiling of the upper, or <see langword="null"/> when either leaves the carrier.</summary>
    /// <param name="lower">The lower numerator.</param>
    /// <param name="upper">The upper numerator.</param>
    /// <param name="denominator">The positive denominator.</param>
    /// <returns>The endpoints, or <see langword="null"/>.</returns>
    public static (long Lower, long Upper)? DirectedHull(BigInteger lower, BigInteger upper, BigInteger denominator) =>
        ExactHull(
            lower: FloorDivide(denominator: denominator, numerator: lower),
            upper: -FloorDivide(denominator: denominator, numerator: -upper)
        );
    /// <summary>The reference interval product: the floor of the least of the four exact corner products and the
    /// ceiling of the greatest, each read at Q16 from the Q32 product, or <see langword="null"/> when that hull leaves
    /// the carrier.</summary>
    /// <returns>The reference endpoints, or <see langword="null"/>.</returns>
    public static (long Lower, long Upper)? IntervalProduct(long leftLower, long leftUpper, long rightLower, long rightUpper) {
        BigInteger[] corners = [
            (((BigInteger)leftLower) * rightLower),
            (((BigInteger)leftLower) * rightUpper),
            (((BigInteger)leftUpper) * rightLower),
            (((BigInteger)leftUpper) * rightUpper),
        ];
        var least = corners[0];
        var greatest = corners[0];

        foreach (var corner in corners) {
            least = BigInteger.Min(left: least, right: corner);
            greatest = BigInteger.Max(left: greatest, right: corner);
        }

        return DirectedHull(denominator: (BigInteger.One << 16), lower: least, upper: greatest);
    }
    /// <summary>The reference interval quotient: over a divisor excluding zero, the floor of the least exact corner
    /// quotient and the ceiling of the greatest; <see langword="null"/> when the divisor holds zero or the hull leaves
    /// the carrier.</summary>
    /// <returns>The reference endpoints, or <see langword="null"/>.</returns>
    public static (long Lower, long Upper)? IntervalQuotient(long leftLower, long leftUpper, long rightLower, long rightUpper) {
        if ((rightLower <= 0L) && (rightUpper >= 0L)) {
            return null;
        }

        BigInteger? lower = null;
        BigInteger? upper = null;

        foreach (var numerator in ((long[])[leftLower, leftUpper])) {
            foreach (var denominator in ((long[])[rightLower, rightUpper])) {
                // n·2¹⁶/d with the sign carried by the numerator so the denominator stays positive.
                var scaled = (((BigInteger)numerator) << 16);
                var positive = BigInteger.Abs(value: denominator);
                var signed = ((denominator < 0L)
                    ? -scaled
                    : scaled);
                var floor = FloorDivide(denominator: positive, numerator: signed);
                var ceiling = -FloorDivide(denominator: positive, numerator: -signed);

                lower = ((lower is { } least) ? BigInteger.Min(left: least, right: floor) : floor);
                upper = ((upper is { } most) ? BigInteger.Max(left: most, right: ceiling) : ceiling);
            }
        }

        return ExactHull(lower: lower!.Value, upper: upper!.Value);
    }
    /// <summary>The reference interval root at Q16: the floor root of the lower endpoint and the ceiling root of the
    /// upper, a non-positive radicand reading zero. The root of a carrier raw never leaves the carrier.</summary>
    /// <returns>The reference endpoints.</returns>
    public static (long Lower, long Upper) IntervalRoot(long lower, long upper) {
        var low = IntegerSquareRoot(value: (((BigInteger)Math.Max(val1: 0L, val2: lower)) << 16));
        var radicand = (((BigInteger)Math.Max(val1: 0L, val2: upper)) << 16);
        var high = IntegerSquareRoot(value: radicand);

        if ((high * high) != radicand) {
            ++high;
        }

        return (((long)low), ((long)high));
    }
    /// <summary>The reference interval norm over a box of raw intervals: the floor root of the least exact sum of
    /// squares and the ceiling root of the greatest, or <see langword="null"/> when that root leaves the carrier.</summary>
    /// <param name="sides">The box's sides as (lower, upper) raw pairs.</param>
    /// <returns>The reference endpoints, or <see langword="null"/>.</returns>
    public static (long Lower, long Upper)? IntervalMagnitude(ReadOnlySpan<(long Lower, long Upper)> sides) {
        var least = BigInteger.Zero;
        var greatest = BigInteger.Zero;

        foreach (var (lower, upper) in sides) {
            BigInteger low = lower;
            BigInteger high = upper;

            if ((lower > 0L) || (upper < 0L)) {
                var near = BigInteger.Min(left: BigInteger.Abs(value: low), right: BigInteger.Abs(value: high));

                least += (near * near);
            }

            var far = BigInteger.Max(left: BigInteger.Abs(value: low), right: BigInteger.Abs(value: high));

            greatest += (far * far);
        }

        var root = IntegerSquareRoot(value: greatest);

        if ((root * root) != greatest) {
            ++root;
        }

        return ExactHull(lower: IntegerSquareRoot(value: least), upper: root);
    }
    /// <summary>An enclosure of <c>acos(v)·2^(16 + guardBitCount)</c> for <c>v = raw / 2¹⁶ ∈ [−1, 1]</c>, as the angle of
    /// the point <c>(v, √(1 − v²))</c>: the root is bracketed by its floor and ceiling at Q40 and the angle of each
    /// bracket enclosed by the arctangent series, whose hull holds the exact angle because the angle is monotone in the
    /// ordinate.</summary>
    /// <param name="raw">The operand's raw, in <c>[−2¹⁶, 2¹⁶]</c>.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    public static Enclosure EncloseArcCosine(long raw, int guardBitCount) {
        var (floor, ceiling) = UnitComplementRootQ40(raw: raw);
        var abscissa = (raw << 24);
        var atFloor = EncloseAtan2(guardBitCount: guardBitCount, xRaw: abscissa, yRaw: floor);
        var atCeiling = EncloseAtan2(guardBitCount: guardBitCount, xRaw: abscissa, yRaw: ceiling);

        return new(
            Low: BigInteger.Min(left: atFloor.Low, right: atCeiling.Low),
            High: BigInteger.Max(left: atFloor.High, right: atCeiling.High)
        );
    }
    /// <summary>An enclosure of <c>asin(v)·2^(16 + guardBitCount)</c>, the angle of the point <c>(√(1 − v²), v)</c>
    /// bracketed as <see cref="EncloseArcCosine"/> brackets it.</summary>
    /// <param name="raw">The operand's raw, in <c>[−2¹⁶, 2¹⁶]</c>.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    public static Enclosure EncloseArcSine(long raw, int guardBitCount) {
        var (floor, ceiling) = UnitComplementRootQ40(raw: raw);
        var ordinate = (raw << 24);
        var atFloor = EncloseAtan2(guardBitCount: guardBitCount, xRaw: floor, yRaw: ordinate);
        var atCeiling = EncloseAtan2(guardBitCount: guardBitCount, xRaw: ceiling, yRaw: ordinate);

        return new(
            Low: BigInteger.Min(left: atFloor.Low, right: atCeiling.Low),
            High: BigInteger.Max(left: atFloor.High, right: atCeiling.High)
        );
    }
    /// <summary>Whether the exact angle <c>(2k + offset)·π/2</c> provably lies inside the real interval
    /// <c>[lower, upper]·2⁻¹⁶</c> for some k, decided against the 384-bit circle enclosure; a candidate the enclosure
    /// cannot place on one side is reported as neither, so the answer is a certainty, never a guess.</summary>
    /// <param name="lower">The interval's lower raw.</param>
    /// <param name="upper">The interval's upper raw.</param>
    /// <param name="offset">One for the sine's extrema, zero for the cosine's.</param>
    /// <returns>Whether a crest (k even) and whether a trough (k odd) provably lies inside.</returns>
    public static (bool Crest, bool Trough) ProvablyReachesExtrema(long lower, long upper, int offset) {
        const int Bits = 384;
        var circle = Pi(bitCount: Bits);
        var low = (((BigInteger)lower) << ((Bits - 16) + 1));
        var high = (((BigInteger)upper) << ((Bits - 16) + 1));
        var first = (BigInteger.Divide(dividend: low, divisor: (2 * circle.Low)) - 2);
        var crest = false;
        var trough = false;

        for (var k = first; (k <= (first + 6)); ++k) {
            var multiple = ((2 * k) + offset);
            var positionLow = BigInteger.Min(left: (multiple * circle.Low), right: (multiple * circle.High));
            var positionHigh = BigInteger.Max(left: (multiple * circle.Low), right: (multiple * circle.High));

            if ((positionLow >= low) && (positionHigh <= high)) {
                if (k.IsEven) {
                    crest = true;
                } else {
                    trough = true;
                }
            }
        }

        return (crest, trough);
    }
    /// <summary>An enclosure of <c>x^y·2^(16 + guardBitCount)</c> for a positive base raw and a positive exponent raw,
    /// as <c>2^(y·log₂ x)</c>: the logarithm enclosed by the repeated-squaring series at 2⁻⁵⁶, the product floored and
    /// ceilinged to a 48-bit exponent, and each side's exponential enclosed by the square-root ladder — the route
    /// <c>scalar.pow-envelope</c>'s oracle takes, sharing nothing with the subject's kernels.</summary>
    /// <param name="baseRaw">The base's raw, positive.</param>
    /// <param name="exponentRaw">The exponent's raw, positive.</param>
    /// <returns>The enclosure at guard scale.</returns>
    public static Enclosure EnclosePow(long baseRaw, long exponentRaw) {
        const int LogarithmGuardBitCount = 40;
        const int ExponentBitCount = 48;
        var logarithm = EncloseLog2(guardBitCount: LogarithmGuardBitCount, raw: baseRaw);
        var first = (logarithm.Low * exponentRaw);
        var second = (logarithm.High * exponentRaw);
        var dropped = (((16 + 16) + LogarithmGuardBitCount) - ExponentBitCount);

        return new(
            Low: EncloseExp2(exponentBitCount: ExponentBitCount, guardBitCount: GuardBitCount, scaledExponent: (BigInteger.Min(left: first, right: second) >> dropped)).Low,
            High: EncloseExp2(exponentBitCount: ExponentBitCount, guardBitCount: GuardBitCount, scaledExponent: -((-BigInteger.Max(left: first, right: second)) >> dropped)).High
        );
    }

    private static BigInteger FloorDivide(BigInteger numerator, BigInteger denominator) {
        var floor = BigInteger.Divide(dividend: numerator, divisor: denominator);

        return (((floor * denominator) > numerator)
            ? (floor - 1)
            : floor);
    }
    // √(1 − v²)·2⁴⁰ for v = raw/2¹⁶, floored and ceilinged: (2³² − raw²)·2⁴⁸ is exact.
    private static (long Floor, long Ceiling) UnitComplementRootQ40(long raw) {
        var radicand = (((((BigInteger)1) << 32) - (((BigInteger)raw) * raw)) << 48);
        var floor = IntegerSquareRoot(value: radicand);
        var ceiling = (((floor * floor) == radicand)
            ? floor
            : (floor + 1));

        return (((long)floor), ((long)ceiling));
    }
}
