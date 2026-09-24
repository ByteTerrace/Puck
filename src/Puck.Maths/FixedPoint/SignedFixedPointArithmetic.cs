using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>The shared signed 64-bit fixed-point arithmetic whose control flow is independent of the binary point.</summary>
internal static class SignedFixedPointArithmetic {
    // The largest power-of-two-grid double strictly below 2^63 and the exactly representable -2^63; clamping a scaled
    // double here keeps the (long) cast from wrapping. Carrier-width facts, independent of the binary point.
    private const double ScaledMaximum = 9223372036854774784d;
    private const double ScaledMinimum = -9223372036854775808d;

    /// <summary>Divides two values in fixed point, rounding to nearest with ties to even and wrapping the rounded
    /// quotient to the signed carrier.</summary>
    /// <typeparam name="TSelf">The carrier.</typeparam>
    /// <param name="dividend">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The rounded quotient.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    internal static TSelf Divide<TSelf>(TSelf dividend, TSelf divisor) where TSelf : struct, ISignedFixedPointFormat<TSelf> {
        var fractionBitCount = TSelf.FractionBitCount;
        var integerBitCount = (64 - fractionBitCount);
        var x = dividend.Value;
        var y = divisor.Value;
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
            var wideDividend = (((UInt128)xMagnitude) << fractionBitCount);
            var quotient128 = (wideDividend / yMagnitude);

            quotient = unchecked((ulong)quotient128);
            remainder = ((ulong)(wideDividend - (quotient128 * yMagnitude)));
        }

        if (
            (remainder > (yMagnitude - remainder)) ||
            ((remainder == (yMagnitude - remainder)) && ((quotient & 1UL) != 0UL))
        ) {
            ++quotient;
        }

        var result = unchecked((long)quotient);
        var resultSign = signX ^ signY;

        return TSelf.FromRawBits(value: unchecked(((result ^ resultSign) - resultSign)));
    }
    /// <summary>Divides two signed raws at the supplied fixed-point split, rounding to nearest with ties to even and
    /// throwing when the rounded quotient leaves the signed carrier.</summary>
    internal static long DivideChecked(long x, long y, int fractionBitCount) {
        var signX = (x >> 63);
        var signY = (y >> 63);
        var xMagnitude = unchecked((ulong)((x ^ signX) - signX));
        var yMagnitude = unchecked((ulong)((y ^ signY) - signY));
        var high = (xMagnitude >> (64 - fractionBitCount));
        UInt128 quotient;
        ulong remainder;

        // The same hardware lane Divide takes: whenever the 128-bit dividend's high word is below the divisor the
        // quotient fits 64 bits, which covers every quotient this method can return without throwing.
        if (
            X86Base.X64.IsSupported &&
            (high < yMagnitude)
        ) {
#pragma warning disable SYSLIB5004
            var (narrowQuotient, narrowRemainder) = X86Base.X64.DivRem(
                divisor: yMagnitude,
                lower: unchecked((xMagnitude << fractionBitCount)),
                upper: high
            );
#pragma warning restore SYSLIB5004
            quotient = narrowQuotient;
            remainder = narrowRemainder;
        } else {
            var dividend = (((UInt128)xMagnitude) << fractionBitCount);

            quotient = (dividend / yMagnitude);
            remainder = ((ulong)(dividend - (quotient * yMagnitude)));
        }

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
    /// <summary>Interpolates from <paramref name="from"/> to <paramref name="to"/> by <paramref name="amount"/>,
    /// forming the whole expression as one exact wide intermediate, rounding it back to the carrier's split exactly
    /// once — to nearest with ties to even — and wrapping the rounded result to the signed carrier.</summary>
    /// <typeparam name="TSelf">The carrier.</typeparam>
    /// <param name="from">The value at an amount of zero.</param>
    /// <param name="to">The value at an amount of one.</param>
    /// <param name="amount">The interpolation amount.</param>
    /// <returns>The rounded interpolation.</returns>
    internal static TSelf Lerp<TSelf>(TSelf from, TSelf to, TSelf amount) where TSelf : struct, ISignedFixedPointFormat<TSelf> {
        // Writing f for fractionBitCount: from·2^f (exact, scale 2^2f) plus (to·amount − from·amount) (exact, scale
        // 2^2f) — the same (to − from)·amount term, just formed as a difference of two products rather than a product
        // of a difference, so it never routes through a standalone raw subtraction that could leave the carrier's
        // range before the multiply even runs. One combine, one round-and-shift back to the caller's scale
        // (ScaleProductSum), so the whole expression rounds once. Nothing here depends on the binary point beyond f.
        var fractionBitCount = TSelf.FractionBitCount;
        var rawOne = (1L << fractionBitCount); // the raw representation of 1.0, in the value domain
        var scaledFrom = FusedArithmetic.Product(
            left: from.Value,
            right: rawOne
        );
        var delta = FusedArithmetic.AddProducts(
            firstLeft: to.Value,
            firstRight: amount.Value,
            secondLeft: from.Value,
            secondRight: amount.Value,
            subtractSecond: true
        );
        var sum = FusedArithmetic.CombineSigned(
            firstMagnitude: scaledFrom.Magnitude,
            firstNegative: scaledFrom.Negative,
            secondMagnitude: delta.Magnitude,
            secondNegative: delta.Negative
        );

        return TSelf.FromRawBits(value: FusedArithmetic.ScaleProductSum(
            shift: -fractionBitCount,
            value: sum
        ));
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
    /// <summary>Multiplies two signed raws at the supplied fixed-point split, rounding to nearest with ties to even
    /// and wrapping the rounded product to the signed carrier.</summary>
    internal static long Multiply(long x, long y, int fractionBitCount) {
        // Narrow lane: both magnitudes below 2^31 keep the exact product below 2^62, inside a signed long, so the
        // rounding runs on machine words. Bit-identical to the wide lane — same exact product, same ties-to-even shift.
        const ulong NarrowLimit = (1UL << 31);

        if ((FusedArithmetic.RawMagnitude(value: x) | FusedArithmetic.RawMagnitude(value: y)) < NarrowLimit) {
            return RoundProductNarrow(
                fractionBitCount: fractionBitCount,
                product: unchecked((x * y))
            );
        }

        return FusedArithmetic.ScaleProductSum(
            value: FusedArithmetic.Product(
                left: x,
                right: y
            ),
            shift: -fractionBitCount
        );
    }

    // Rounds an exact signed product that fits a long to the supplied split, to nearest with ties to even, on the
    // magnitude; the sign is reapplied afterwards, so the discipline matches ScaleProductSum's sign-magnitude form.
    private static long RoundProductNarrow(long product, int fractionBitCount) {
        var sign = (product >> 63);
        var magnitude = unchecked((ulong)((product ^ sign) - sign));
        var truncated = (magnitude >> fractionBitCount);
        var remainder = magnitude & ((1UL << fractionBitCount) - 1UL);
        var half = (1UL << (fractionBitCount - 1));

        if (
            (remainder > half) ||
            ((remainder == half) && (0UL != (truncated & 1UL)))
        ) {
            ++truncated;
        }

        var result = unchecked((long)truncated);

        return unchecked(((result ^ sign) - sign));
    }

    /// <summary>Multiplies two signed raws at the supplied fixed-point split, rounding to nearest with ties to even
    /// and throwing when the rounded product leaves the signed carrier.</summary>
    internal static long MultiplyChecked(long x, long y, int fractionBitCount) {
        var product = FusedArithmetic.Product(
            left: x,
            right: y
        );
        var scaled = FusedArithmetic.ScaleMagnitudeToNearest(
            magnitude: product.Magnitude,
            shift: -fractionBitCount
        );

        return FromCheckedMagnitude(
            magnitude: scaled.Magnitude,
            negative: product.Negative
        );
    }
    /// <summary>Returns the magnitude of <paramref name="value"/> carrying the sign of <paramref name="sign"/>.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long CopySign(long value, long sign, string typeName) {
        if (
            (value == long.MinValue) &&
            (sign >= 0L)
        ) {
            throw new OverflowException(message: $"The positive magnitude of {typeName}.MinValue is not representable.");
        }

        var magnitudeSign = (value >> 63);
        var magnitude = unchecked(((value ^ magnitudeSign) - magnitudeSign));
        var targetSign = (sign >> 63);

        return unchecked(((magnitude ^ targetSign) - targetSign));
    }
    /// <summary>Converts a <see cref="double"/> to a fixed-point value, rounding to nearest with ties to even.</summary>
    /// <typeparam name="TSelf">The carrier.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <returns>The nearest representable value, clamped to the carrier's range; not-a-number clamps to zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static TSelf FromDouble<TSelf>(double value) where TSelf : struct, ISignedFixedPointFormat<TSelf> {
        var scaled = double.Round(
            mode: MidpointRounding.ToEven,
            x: (value * ((double)(1L << TSelf.FractionBitCount)))
        );

        if (double.IsNaN(d: scaled)) { return TSelf.FromRawBits(value: 0L); }
        if (scaled > ScaledMaximum) { return TSelf.FromRawBits(value: long.MaxValue); }
        if (scaled <= ScaledMinimum) { return TSelf.FromRawBits(value: long.MinValue); }

        return TSelf.FromRawBits(value: unchecked((long)scaled));
    }
    /// <summary>Rounds a value to the nearest integral value, with ties rounded to the nearest even integer.</summary>
    /// <typeparam name="TSelf">The carrier.</typeparam>
    /// <param name="value">The value to round.</param>
    /// <returns>The rounded value.</returns>
    /// <exception cref="OverflowException">The rounded result exceeds the carrier's maximum.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static TSelf Round<TSelf>(TSelf value) where TSelf : struct, ISignedFixedPointFormat<TSelf> {
        var fractionBitCount = TSelf.FractionBitCount;
        var fractionBitMask = ((1UL << fractionBitCount) - 1UL);
        var rawHalf = (1UL << (fractionBitCount - 1));
        var integerPart = value.Value & unchecked((long)~fractionBitMask);
        var fraction = ((ulong)value.Value) & fractionBitMask;
        var roundUp = ((fraction > rawHalf) || ((fraction == rawHalf) && (((integerPart >> fractionBitCount) & 1L) != 0L)));

        return TSelf.FromRawBits(value: (roundUp
            ? checked((integerPart + (1L << fractionBitCount)))
            : integerPart));
    }
    /// <summary>Compares an instance with a boxed object and indicates relative order.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static int CompareToObject<T>(T self, object? obj) where T : struct, IComparable<T> {
        if (obj is null) { return 1; }
        if (obj is T other) { return self.CompareTo(other: other); }

        throw new ArgumentException(
            message: $"Object must be of type {typeof(T).Name}.",
            paramName: nameof(obj)
        );
    }
    /// <summary>Returns the integral part of a fixed-point raw, discarding the fraction (rounding toward zero).</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long Truncate(long rawValue, long integerBitMask, ulong fractionBitMask, long rawOne) {
        var floor = rawValue & integerBitMask;

        return (((rawValue < 0L) && ((rawValue & ((long)fractionBitMask)) != 0L))
            ? unchecked((floor + rawOne))
            : floor);
    }
    /// <summary>Returns the smallest integral value greater than or equal to the fixed-point raw.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long Ceiling(long rawValue, long integerBitMask, ulong fractionBitMask, long rawOne) {
        var floor = rawValue & integerBitMask;

        return ((((rawValue & ((long)fractionBitMask)) != 0L))
            ? checked((floor + rawOne))
            : floor);
    }
    /// <summary>Computes the remainder of x divided by y, avoiding signed overflow traps for division by ±1.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long Modulo(long x, long y) {
        if ((y == 1L) || (y == -1L)) {
            return 0L;
        }

        return (x % y);
    }
    /// <summary>Scales an integer value into a fixed-point raw, throwing if outside the valid integer bounds.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long FromInteger(long value, long maxIntegerValue, long minIntegerValue, int fractionBitCount) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: maxIntegerValue,
            value: value
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: minIntegerValue,
            value: value
        );

        return (value << fractionBitCount);
    }
    /// <summary>Narrows a wider fixed-point raw representation to FixedQ4816 with round-half-to-even.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long NarrowToFixedQ4816(long value, int narrowShift, ulong narrowBitMask, ulong narrowHalf) {
        var sign = (value >> 63);
        var magnitude = unchecked((ulong)((value ^ sign) - sign));
        var truncated = (magnitude >> narrowShift);
        var remainder = magnitude & narrowBitMask;
        var rounded = FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: narrowHalf,
            truncated: truncated
        );
        var result = unchecked((long)rounded);

        return ((sign != 0L)
            ? unchecked(-result)
            : result);
    }
    /// <summary>Widens a FixedQ4816 raw value into a wider format, reporting overflow.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static bool TryWidenFromFixedQ4816(long rawValue, int widenShift, out long result) {
        var widened = (((Int128)rawValue) << widenShift);

        if (
            (widened < long.MinValue) ||
            (widened > long.MaxValue)
        ) {
            result = default;

            return false;
        }

        result = ((long)widened);

        return true;
    }

    internal delegate bool TryParseSpanDelegate<T>(ReadOnlySpan<char> s, IFormatProvider? provider, out T result);

    /// <summary>Parses a character span using the supplied parser delegate or throws a FormatException.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static T Parse<T>(ReadOnlySpan<char> s, IFormatProvider? provider, TryParseSpanDelegate<T> tryParse) {
        if (!tryParse(s, provider, out var result)) {
            throw new FormatException(message: $"The input span was not in a valid {typeof(T).Name} format.");
        }

        return result;
    }
    /// <summary>Tries to parse a string using the supplied parser delegate, returning false when null.</summary>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseString<T>(string? s, IFormatProvider? provider, TryParseSpanDelegate<T> tryParse, out T result) {
        if (s is null) {
            result = default!;

            return false;
        }

        return tryParse(s.AsSpan(), provider, out result);
    }
}
