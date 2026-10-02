using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The directed rounding of an exact rational to a raw: its floor or its ceiling, saturated to the signed
    /// carrier, where the extremes stand for the unbounded ends of an interval.</summary>
    /// <param name="numerator">The exact value's numerator, in raw units.</param>
    /// <param name="denominator">The exact value's positive denominator.</param>
    /// <param name="ceiling">Whether to round up rather than down.</param>
    /// <returns>The saturated directed raw.</returns>
    public static long DirectedRaw(BigInteger numerator, BigInteger denominator, bool ceiling) {
        var floor = BigInteger.Divide(dividend: numerator, divisor: denominator);

        if ((floor * denominator) > numerator) {
            --floor;
        }

        var rounded = ((ceiling && ((floor * denominator) != numerator))
            ? (floor + 1)
            : floor);

        return SaturateRaw(value: rounded);
    }
    /// <summary>Saturates an exact integer to the signed 64-bit carrier.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The value, or the nearer carrier extreme.</returns>
    public static long SaturateRaw(BigInteger value) =>
        ((value > long.MaxValue)
            ? long.MaxValue
            : ((value < long.MinValue)
                ? long.MinValue
                : ((long)value)));
    /// <summary>The reference interval product: over bounded operands, the floor of the least of the four exact
    /// corner products and the ceiling of the greatest, each read at Q16 from the Q32 product; zero when either operand
    /// is exactly zero; and the whole carrier when either operand reaches a carrier extreme.</summary>
    /// <returns>The reference endpoints.</returns>
    public static (long Lower, long Upper) IntervalProduct(long leftLower, long leftUpper, long rightLower, long rightUpper) {
        if (((leftLower == 0L) && (leftUpper == 0L)) || ((rightLower == 0L) && (rightUpper == 0L))) {
            return (0L, 0L);
        }

        if (IsCarrierExtreme(lower: leftLower, upper: leftUpper) || IsCarrierExtreme(lower: rightLower, upper: rightUpper)) {
            return (long.MinValue, long.MaxValue);
        }

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

        return (
            DirectedRaw(ceiling: false, denominator: (BigInteger.One << 16), numerator: least),
            DirectedRaw(ceiling: true, denominator: (BigInteger.One << 16), numerator: greatest)
        );
    }
    /// <summary>The reference interval quotient: over bounded operands whose divisor excludes zero, the floor of the
    /// least exact corner quotient and the ceiling of the greatest; otherwise the whole carrier.</summary>
    /// <returns>The reference endpoints.</returns>
    public static (long Lower, long Upper) IntervalQuotient(long leftLower, long leftUpper, long rightLower, long rightUpper) {
        if (
            ((rightLower <= 0L) && (rightUpper >= 0L)) ||
            IsCarrierExtreme(lower: leftLower, upper: leftUpper) ||
            IsCarrierExtreme(lower: rightLower, upper: rightUpper)
        ) {
            return (long.MinValue, long.MaxValue);
        }

        var lower = long.MaxValue;
        var upper = long.MinValue;

        foreach (var numerator in ((long[])[leftLower, leftUpper])) {
            foreach (var denominator in ((long[])[rightLower, rightUpper])) {
                // n·2¹⁶/d with the sign carried by the numerator so the denominator stays positive.
                var scaled = (((BigInteger)numerator) << 16);
                var positive = BigInteger.Abs(value: denominator);
                var signed = ((denominator < 0L)
                    ? -scaled
                    : scaled);

                lower = Math.Min(val1: lower, val2: DirectedRaw(ceiling: false, denominator: positive, numerator: signed));
                upper = Math.Max(val1: upper, val2: DirectedRaw(ceiling: true, denominator: positive, numerator: signed));
            }
        }

        return (lower, upper);
    }
    /// <summary>The reference interval root at Q16: the floor root of the lower endpoint and the ceiling root of the
    /// upper, a non-positive radicand reading zero, and an unbounded upper endpoint staying unbounded.</summary>
    /// <returns>The reference endpoints.</returns>
    public static (long Lower, long Upper) IntervalRoot(long lower, long upper) {
        var low = IntegerSquareRoot(value: (((BigInteger)Math.Max(val1: 0L, val2: lower)) << 16));

        if (upper == long.MaxValue) {
            return (SaturateRaw(value: low), long.MaxValue);
        }

        var radicand = (((BigInteger)Math.Max(val1: 0L, val2: upper)) << 16);
        var high = IntegerSquareRoot(value: radicand);

        if ((high * high) != radicand) {
            ++high;
        }

        return (SaturateRaw(value: low), SaturateRaw(value: high));
    }
    /// <summary>The reference interval norm over a box of raw intervals: the floor root of the least exact sum of
    /// squares and the ceiling root of the greatest, unbounded above when any side is.</summary>
    /// <param name="sides">The box's sides as (lower, upper) raw pairs.</param>
    /// <returns>The reference endpoints.</returns>
    public static (long Lower, long Upper) IntervalMagnitude(ReadOnlySpan<(long Lower, long Upper)> sides) {
        var least = BigInteger.Zero;
        var greatest = BigInteger.Zero;
        var unbounded = false;

        foreach (var (lower, upper) in sides) {
            BigInteger low = lower;
            BigInteger high = upper;

            if ((lower > 0L) || (upper < 0L)) {
                var near = BigInteger.Min(left: BigInteger.Abs(value: low), right: BigInteger.Abs(value: high));

                least += (near * near);
            }

            var far = BigInteger.Max(left: BigInteger.Abs(value: low), right: BigInteger.Abs(value: high));

            greatest += (far * far);
            unbounded |= IsCarrierExtreme(lower: lower, upper: upper);
        }

        var root = IntegerSquareRoot(value: greatest);

        if ((root * root) != greatest) {
            ++root;
        }

        return (
            SaturateRaw(value: IntegerSquareRoot(value: least)),
            (unbounded
                ? long.MaxValue
                : SaturateRaw(value: root))
        );
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

    private static bool IsCarrierExtreme(long lower, long upper) =>
        ((lower == long.MinValue) || (upper == long.MaxValue));
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
