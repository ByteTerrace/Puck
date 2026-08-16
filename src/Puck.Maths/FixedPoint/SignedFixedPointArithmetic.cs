using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>The shared signed 64-bit fixed-point arithmetic whose control flow is independent of the binary point.</summary>
internal static class SignedFixedPointArithmetic {
    /// <summary>Divides two signed raws at the supplied fixed-point split, rounding to nearest with ties to even and
    /// wrapping the rounded quotient to the signed carrier.</summary>
    internal static long Divide(long x, long y, int fractionBitCount, int integerBitCount) {
        var signX = (x >> 63);
        var signY = (y >> 63);
        var xMagnitude = unchecked((ulong)((x ^ signX) - signX));
        var yMagnitude = unchecked((ulong)((y ^ signY) - signY));
        var high = (xMagnitude >> integerBitCount);
        ulong quotient;
        ulong remainder;

        if (
            X86Base.X64.IsSupported &&
            (high < yMagnitude)
        ) {
#pragma warning disable SYSLIB5004
            (quotient, remainder) = X86Base.X64.DivRem(
                divisor: yMagnitude,
                lower: unchecked((xMagnitude << fractionBitCount)),
                upper: high
            );
#pragma warning restore SYSLIB5004
        } else {
            var dividend = (((UInt128)xMagnitude) << fractionBitCount);
            var quotient128 = (dividend / yMagnitude);

            quotient = unchecked((ulong)quotient128);
            remainder = ((ulong)(dividend - (quotient128 * yMagnitude)));
        }

        if (
            (remainder > (yMagnitude - remainder)) ||
            ((remainder == (yMagnitude - remainder)) && ((quotient & 1UL) != 0UL))
        ) {
            ++quotient;
        }

        var result = unchecked((long)quotient);
        var resultSign = signX ^ signY;

        return unchecked(((result ^ resultSign) - resultSign));
    }
    /// <summary>Divides two signed raws at the supplied fixed-point split, rounding to nearest with ties to even and
    /// throwing when the rounded quotient leaves the signed carrier.</summary>
    internal static long DivideChecked(long x, long y, int fractionBitCount) {
        var signX = (x >> 63);
        var signY = (y >> 63);
        var xMagnitude = unchecked((ulong)((x ^ signX) - signX));
        var yMagnitude = unchecked((ulong)((y ^ signY) - signY));
        var dividend = (((UInt128)xMagnitude) << fractionBitCount);
        var quotient = (dividend / yMagnitude);
        var remainder = ((ulong)(dividend - (quotient * yMagnitude)));

        if (
            (remainder > (yMagnitude - remainder)) ||
            ((remainder == (yMagnitude - remainder)) && ((quotient & UInt128.One) != UInt128.Zero))
        ) {
            ++quotient;
        }

        return FromCheckedMagnitude(
            magnitude: quotient,
            negative: ((signX ^ signY) != 0L)
        );
    }
    /// <summary>Applies a sign to an unsigned magnitude, throwing when it leaves the signed 64-bit carrier.</summary>
    internal static long FromCheckedMagnitude(UInt128 magnitude, bool negative) {
        var negativeLimit = (UInt128.One << 63);

        if (negative) {
            if (magnitude > negativeLimit) { throw new OverflowException(); }
            if (magnitude == negativeLimit) { return long.MinValue; }

            return -checked((long)magnitude);
        }

        return checked((long)magnitude);
    }
    /// <summary>Interpolates from <paramref name="from"/> to <paramref name="to"/> by <paramref name="amount"/> at the
    /// supplied fixed-point split, forming the whole expression as one exact wide intermediate, rounding it back to
    /// that split exactly once — to nearest with ties to even — and wrapping the rounded result to the signed
    /// carrier.</summary>
    internal static long Lerp(long from, long to, long amount, int fractionBitCount) {
        // Writing f for fractionBitCount: from·2^f (exact, scale 2^2f) plus (to·amount − from·amount) (exact, scale
        // 2^2f) — the same (to − from)·amount term, just formed as a difference of two products rather than a product
        // of a difference, so it never routes through a standalone raw subtraction that could leave the carrier's
        // range before the multiply even runs. One combine, one round-and-shift back to the caller's scale
        // (ScaleProductSum), so the whole expression rounds once. Nothing here depends on the binary point beyond f.
        var rawOne = (1L << fractionBitCount); // the raw representation of 1.0, in the value domain
        var scaledFrom = FusedArithmetic.Product(
            left: from,
            right: rawOne
        );
        var delta = FusedArithmetic.AddProducts(
            firstLeft: to,
            firstRight: amount,
            secondLeft: from,
            secondRight: amount,
            subtractSecond: true
        );
        var sum = FusedArithmetic.CombineSigned(
            firstMagnitude: scaledFrom.Magnitude,
            firstNegative: scaledFrom.Negative,
            secondMagnitude: delta.Magnitude,
            secondNegative: delta.Negative
        );

        return FusedArithmetic.ScaleProductSum(
            shift: -fractionBitCount,
            value: sum
        );
    }
    /// <summary>Returns whichever of two signed raws has the larger magnitude, resolving a magnitude tie toward the
    /// non-negative one — <see cref="System.Numerics.INumberBase{TSelf}.MaxMagnitude"/>'s rule.</summary>
    internal static long MaximumMagnitude(long x, long y) {
        var xMagnitude = FusedArithmetic.RawMagnitude(value: x);
        var yMagnitude = FusedArithmetic.RawMagnitude(value: y);

        return (((xMagnitude > yMagnitude) || ((xMagnitude == yMagnitude) && (x >= 0L)))
            ? x
            : y
        );
    }
    /// <summary>Returns whichever of two signed raws has the smaller magnitude, resolving a magnitude tie toward the
    /// negative one — <see cref="System.Numerics.INumberBase{TSelf}.MinMagnitude"/>'s rule, which picks the operand
    /// <see cref="MaximumMagnitude"/> would not.</summary>
    internal static long MinimumMagnitude(long x, long y) {
        var xMagnitude = FusedArithmetic.RawMagnitude(value: x);
        var yMagnitude = FusedArithmetic.RawMagnitude(value: y);

        return (((xMagnitude < yMagnitude) || ((xMagnitude == yMagnitude) && (x < 0L)))
            ? x
            : y
        );
    }
}
