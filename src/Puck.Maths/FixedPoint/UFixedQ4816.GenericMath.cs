using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

public readonly partial record struct UFixedQ4816 {
    public static int Radix => 2;

    public static UFixedQ4816 operator +(UFixedQ4816 value) => value;

    public static bool IsCanonical(UFixedQ4816 value) => true;
    public static bool IsComplexNumber(UFixedQ4816 value) => false;
    public static bool IsEvenInteger(UFixedQ4816 value) =>
        (((value.Value & ((1UL << FractionBitCount) - 1UL)) == 0UL) &&
        (((value.Value >> FractionBitCount) & 1UL) == 0UL));
    public static bool IsFinite(UFixedQ4816 value) => true;
    public static bool IsImaginaryNumber(UFixedQ4816 value) => false;
    public static bool IsInfinity(UFixedQ4816 value) => false;
    public static bool IsInteger(UFixedQ4816 value) =>
        ((value.Value & ((1UL << FractionBitCount) - 1UL)) == 0UL);
    public static bool IsNaN(UFixedQ4816 value) => false;
    public static bool IsNegative(UFixedQ4816 value) => false;
    public static bool IsNegativeInfinity(UFixedQ4816 value) => false;
    public static bool IsNormal(UFixedQ4816 value) => (value.Value != 0UL);
    public static bool IsOddInteger(UFixedQ4816 value) =>
        (((value.Value & ((1UL << FractionBitCount) - 1UL)) == 0UL) &&
        (((value.Value >> FractionBitCount) & 1UL) != 0UL));
    public static bool IsPositive(UFixedQ4816 value) => true;
    public static bool IsPositiveInfinity(UFixedQ4816 value) => false;
    public static bool IsRealNumber(UFixedQ4816 value) => true;
    public static bool IsSubnormal(UFixedQ4816 value) => false;
    public static bool IsZero(UFixedQ4816 value) => (value.Value == 0UL);
    public static UFixedQ4816 MaxMagnitude(UFixedQ4816 x, UFixedQ4816 y) => Max(x: x, y: y);
    public static UFixedQ4816 MaxMagnitudeNumber(UFixedQ4816 x, UFixedQ4816 y) => Max(x: x, y: y);
    public static UFixedQ4816 MinMagnitude(UFixedQ4816 x, UFixedQ4816 y) => Min(x: x, y: y);
    public static UFixedQ4816 MinMagnitudeNumber(UFixedQ4816 x, UFixedQ4816 y) => Min(x: x, y: y);
    public static UFixedQ4816 Parse(string s, NumberStyles style, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(s: s.AsSpan(), style: style, provider: provider);
    }
    public static UFixedQ4816 Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) {
        var status = ParseText(
            s: s,
            style: style,
            provider: provider,
            result: out var result
        );

        if (FixedPointParseStatus.Success == status) {
            return result;
        }

        if (FixedPointParseStatus.Overflow == status) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(UFixedQ4816)} range.");
        }

        // Re-enter the platform parser only on failure so Parse preserves its FormatException versus
        // decimal-overflow distinction. Successful values are always quantized from their original digits.
        _ = decimal.Parse(s: s, style: style, provider: (provider ?? CultureInfo.InvariantCulture));

        throw new FormatException(message: $"The input span was not in a valid {nameof(UFixedQ4816)} format.");
    }
    public static bool TryParse(string? s, NumberStyles style, IFormatProvider? provider, out UFixedQ4816 result) {
        if (s is null) {
            result = default;

            return false;
        }

        return TryParse(s: s.AsSpan(), style: style, provider: provider, result: out result);
    }
    public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out UFixedQ4816 result) {
        return (FixedPointParseStatus.Success == ParseText(
            s: s,
            style: style,
            provider: provider,
            result: out result
        ));
    }

    private static FixedPointParseStatus ParseText(
        ReadOnlySpan<char> s,
        NumberStyles style,
        IFormatProvider? provider,
        out UFixedQ4816 result
    ) {
        result = default;
        var status = FixedPointText.Parse(
            s: s,
            style: style,
            provider: provider,
            fractionBitCount: FractionBitCount,
            parsingDenominator: ParsingDenominator,
            maximumPositiveRaw: ulong.MaxValue,
            maximumNegativeMagnitudeRaw: 0UL,
            rejectExactOutOfRange: false,
            negative: out _,
            rawMagnitude: out var rawMagnitude
        );

        if (FixedPointParseStatus.Success == status) {
            result = new(Value: rawMagnitude);
        }

        return status;
    }

    static bool INumberBase<UFixedQ4816>.TryConvertFromChecked<TOther>(TOther value, out UFixedQ4816 result) {
        if (typeof(TOther) == typeof(UFixedQ4816)) {
            result = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            result = new(Value: checked((ulong)other.Value));

            return true;
        }

        if (FixedPointConvert.TryGetFloating(value: value, result: out var floating)) {
            result = FromDoubleChecked(value: floating);

            return true;
        }

        if (!FixedPointConvert.IsKnownBclNumeric<TOther>()) {
            result = default;

            return false;
        }

        try {
            result = FromDecimalChecked(value: decimal.CreateChecked(value: value));

            return true;
        } catch (NotSupportedException) {
            result = default;

            return false;
        }
    }
    static bool INumberBase<UFixedQ4816>.TryConvertFromSaturating<TOther>(TOther value, out UFixedQ4816 result) {
        if (typeof(TOther) == typeof(UFixedQ4816)) {
            result = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            result = new(Value: ((other.Value < 0L) ? 0UL : ((ulong)other.Value)));

            return true;
        }

        if (FixedPointConvert.TryGetFloating(value: value, result: out var floating)) {
            result = FromDouble(value: floating);

            return true;
        }

        if (!FixedPointConvert.IsKnownBclNumeric<TOther>()) {
            result = default;

            return false;
        }

        try {
            result = FromDecimalSaturating(value: decimal.CreateSaturating(value: value));

            return true;
        } catch (NotSupportedException) {
            result = default;

            return false;
        }
    }
    static bool INumberBase<UFixedQ4816>.TryConvertFromTruncating<TOther>(TOther value, out UFixedQ4816 result) {
        if (typeof(TOther) == typeof(UFixedQ4816)) {
            result = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            // The peer carries the SAME width and the SAME Q16 scale, so truncation across signedness is the low
            // sixty-four bits verbatim — the clamp the saturating hook applies belongs to that hook alone.
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            result = new(Value: unchecked((ulong)other.Value));

            return true;
        }

        if (FixedPointConvert.TryGetFloating(value: value, result: out var floating)) {
            result = FromDouble(value: floating);

            return true;
        }

        if (FixedPointConvert.TryScaleTruncating(value: value, fractionBitCount: FractionBitCount, out var scaled)) {
            result = new(Value: unchecked((ulong)scaled));

            return true;
        }

        result = default;

        return false;
    }
    static bool INumberBase<UFixedQ4816>.TryConvertToChecked<TOther>(UFixedQ4816 value, out TOther result) {
        if (typeof(TOther) == typeof(UFixedQ4816)) {
            result = Unsafe.As<UFixedQ4816, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var converted = new FixedQ4816(Value: checked((long)value.Value));

            result = Unsafe.As<FixedQ4816, TOther>(source: ref converted);

            return true;
        }

        if (TrySetFloating(value: value, result: out result)) {
            return true;
        }

        if (!FixedPointConvert.IsKnownBclNumeric<TOther>()) {
            result = default!;

            return false;
        }

        try {
            result = TOther.CreateChecked(value: ToDecimal(value: value));

            return true;
        } catch (NotSupportedException) {
            result = default!;

            return false;
        }
    }
    static bool INumberBase<UFixedQ4816>.TryConvertToSaturating<TOther>(UFixedQ4816 value, out TOther result) {
        if (typeof(TOther) == typeof(UFixedQ4816)) {
            result = Unsafe.As<UFixedQ4816, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var converted = new FixedQ4816(Value: ((value.Value > long.MaxValue) ? long.MaxValue : ((long)value.Value)));

            result = Unsafe.As<FixedQ4816, TOther>(source: ref converted);

            return true;
        }

        if (TrySetFloating(value: value, result: out result)) {
            return true;
        }

        if (!FixedPointConvert.IsKnownBclNumeric<TOther>()) {
            result = default!;

            return false;
        }

        try {
            result = TOther.CreateSaturating(value: ToDecimal(value: value));

            return true;
        } catch (NotSupportedException) {
            result = default!;

            return false;
        }
    }
    static bool INumberBase<UFixedQ4816>.TryConvertToTruncating<TOther>(UFixedQ4816 value, out TOther result) {
        if (typeof(TOther) == typeof(UFixedQ4816)) {
            result = Unsafe.As<UFixedQ4816, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            // Same width, same scale: the low sixty-four bits verbatim, mirroring the from-hook so the two agree.
            var converted = new FixedQ4816(Value: unchecked((long)value.Value));

            result = Unsafe.As<FixedQ4816, TOther>(source: ref converted);

            return true;
        }

        if (FixedPointConvert.IsKnownBclInteger<TOther>()) {
            // The integer part, handed to the TARGET's own truncation rather than to decimal's saturating one.
            result = TOther.CreateTruncating(value: ((UInt128)(value.Value >> FractionBitCount)));

            return true;
        }

        if (TrySetFloating(value: value, result: out result)) {
            return true;
        }

        if (!FixedPointConvert.IsKnownBclNumeric<TOther>()) {
            result = default!;

            return false;
        }

        try {
            result = TOther.CreateTruncating(value: ToDecimal(value: value));

            return true;
        } catch (NotSupportedException) {
            result = default!;

            return false;
        }
    }

    private static UFixedQ4816 FromDecimalChecked(decimal value) {
        // The exact single ties-to-even rounding of the decimal's own rational; a decimal multiply would round the
        // rescale first and could resolve a manufactured tie off the true value's side.
        var scaled = FixedPointConvert.ScaleDecimal(value: value, fractionBitCount: FractionBitCount);

        if ((scaled < Int128.Zero) || (scaled > ulong.MaxValue)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(UFixedQ4816)} range.");
        }

        return new(Value: ((ulong)scaled));
    }
    private static UFixedQ4816 FromDoubleChecked(double value) {
        var scaled = double.Round(x: (value * (1UL << FractionBitCount)), mode: MidpointRounding.ToEven);

        if (double.IsNaN(d: scaled) || (scaled < 0d) || (scaled > ScaledMaximum)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(UFixedQ4816)} range.");
        }

        return new(Value: ((ulong)scaled));
    }
    private static UFixedQ4816 FromDecimalSaturating(decimal value) {
        var scaled = FixedPointConvert.ScaleDecimal(value: value, fractionBitCount: FractionBitCount);

        if (scaled < Int128.Zero) { return Zero; }
        if (scaled > ulong.MaxValue) { return MaxValue; }

        return new(Value: ((ulong)scaled));
    }
    private static decimal ToDecimal(UFixedQ4816 value) => (value.Value / ((decimal)(1UL << FractionBitCount)));
    private static bool TrySetFloating<TOther>(UFixedQ4816 value, out TOther result)
        where TOther : INumberBase<TOther> {
        // Each target rounds the raw ONCE: the integer-to-floating conversion is the only lossy step and the
        // power-of-two scale is exact (the smallest nonzero raw lands at 2⁻¹⁶, fully normal in both formats).
        // Routing float through double would round twice, and a double landing exactly on a float midpoint then
        // resolves the tie by parity instead of by the true value's side.
        if (typeof(TOther) == typeof(double)) {
            var wide = ((double)value);

            result = Unsafe.As<double, TOther>(source: ref wide);

            return true;
        }

        if (typeof(TOther) == typeof(float)) {
            var single = MathF.ScaleB(x: ((float)value.Value), n: -FractionBitCount);

            result = Unsafe.As<float, TOther>(source: ref single);

            return true;
        }

        result = default!;

        return false;
    }
}
