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
        ((value.Value & ((1UL << FractionBitCount) - 1UL)) == 0UL) &&
        (((value.Value >> FractionBitCount) & 1UL) == 0UL);
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
        ((value.Value & ((1UL << FractionBitCount) - 1UL)) == 0UL) &&
        (((value.Value >> FractionBitCount) & 1UL) != 0UL);
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

        if (TryGetFloating(value: value, result: out var floating)) {
            result = FromDoubleChecked(value: floating);

            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default;

            return false;
        }

        try {
            result = FromDecimalChecked(value: decimal.CreateChecked(value));

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

        if (TryGetFloating(value: value, result: out var floating)) {
            result = FromDouble(value: floating);

            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default;

            return false;
        }

        try {
            result = FromDecimalSaturating(value: decimal.CreateSaturating(value));

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
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            result = new(Value: ((other.Value < 0L) ? 0UL : ((ulong)other.Value)));

            return true;
        }

        if (TryGetFloating(value: value, result: out var floating)) {
            result = FromDouble(value: floating);

            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default;

            return false;
        }

        try {
            result = FromDecimalSaturating(value: decimal.CreateTruncating(value));

            return true;
        } catch (NotSupportedException) {
            result = default;

            return false;
        }
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

        if (TrySetFloating(value: ((double)value), result: out result)) {
            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
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

        if (TrySetFloating(value: ((double)value), result: out result)) {
            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
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
            var converted = new FixedQ4816(Value: ((value.Value > long.MaxValue) ? long.MaxValue : ((long)value.Value)));

            result = Unsafe.As<FixedQ4816, TOther>(source: ref converted);

            return true;
        }

        if (TrySetFloating(value: ((double)value), result: out result)) {
            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
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
        var scaled = decimal.Round(d: checked(value * (1UL << FractionBitCount)), decimals: 0, mode: MidpointRounding.ToEven);

        return new(Value: checked((ulong)scaled));
    }

    private static UFixedQ4816 FromDoubleChecked(double value) {
        var scaled = double.Round(x: (value * (1UL << FractionBitCount)), mode: MidpointRounding.ToEven);

        if (double.IsNaN(d: scaled) || (scaled < 0d) || (scaled > ScaledMaximum)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(UFixedQ4816)} range.");
        }

        return new(Value: ((ulong)scaled));
    }

    private static UFixedQ4816 FromDecimalSaturating(decimal value) {
        if (value <= 0m) { return Zero; }

        decimal scaled;

        try {
            scaled = decimal.Round(d: checked(value * (1UL << FractionBitCount)), decimals: 0, mode: MidpointRounding.ToEven);
        } catch (OverflowException) {
            return MaxValue;
        }

        if (scaled >= ulong.MaxValue) { return MaxValue; }

        return new(Value: ((ulong)scaled));
    }

    private static decimal ToDecimal(UFixedQ4816 value) => (value.Value / ((decimal)(1UL << FractionBitCount)));

    private static bool IsKnownBclNumeric<TOther>()
        where TOther : INumberBase<TOther> =>
        (typeof(TOther) == typeof(byte)) || (typeof(TOther) == typeof(sbyte)) ||
        (typeof(TOther) == typeof(short)) || (typeof(TOther) == typeof(ushort)) ||
        (typeof(TOther) == typeof(int)) || (typeof(TOther) == typeof(uint)) ||
        (typeof(TOther) == typeof(long)) || (typeof(TOther) == typeof(ulong)) ||
        (typeof(TOther) == typeof(nint)) || (typeof(TOther) == typeof(nuint)) ||
        (typeof(TOther) == typeof(Int128)) || (typeof(TOther) == typeof(UInt128)) ||
        (typeof(TOther) == typeof(char)) || (typeof(TOther) == typeof(decimal)) ||
        (typeof(TOther) == typeof(BigInteger)) || (typeof(TOther) == typeof(Half));

    private static bool TryGetFloating<TOther>(TOther value, out double result)
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(double)) {
            result = Unsafe.As<TOther, double>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(float)) {
            result = Unsafe.As<TOther, float>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(Half)) {
            result = ((double)Unsafe.As<TOther, Half>(source: ref value));

            return true;
        }

        result = default;

        return false;
    }

    private static bool TrySetFloating<TOther>(double value, out TOther result)
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(double)) {
            result = Unsafe.As<double, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(float)) {
            var single = ((float)value);

            result = Unsafe.As<float, TOther>(source: ref single);

            return true;
        }

        result = default!;

        return false;
    }
}
