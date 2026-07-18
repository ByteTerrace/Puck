using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

public readonly partial record struct FixedQ4816 {
    private const NumberStyles DefaultParseStyle = (NumberStyles.AllowLeadingWhite |
                                                     NumberStyles.AllowTrailingWhite |
                                                     NumberStyles.AllowLeadingSign |
                                                     NumberStyles.AllowDecimalPoint);
    private const int MaximumFormattedLength = 34;

    private static readonly UInt128 ParsingDenominator = FixedPointText.CreateParsingDenominator(
        fractionBitCount: FractionBitCount
    );

    public static int Radix => 2;

    public static FixedQ4816 operator +(FixedQ4816 value) => value;

    public static bool IsCanonical(FixedQ4816 value) => true;
    public static bool IsComplexNumber(FixedQ4816 value) => false;
    public static bool IsEvenInteger(FixedQ4816 value) =>
        ((value.Value & ((1L << FractionBitCount) - 1L)) == 0L) &&
        (((value.Value >> FractionBitCount) & 1L) == 0L);
    public static bool IsFinite(FixedQ4816 value) => true;
    public static bool IsImaginaryNumber(FixedQ4816 value) => false;
    public static bool IsInfinity(FixedQ4816 value) => false;
    public static bool IsInteger(FixedQ4816 value) =>
        ((value.Value & ((1L << FractionBitCount) - 1L)) == 0L);
    public static bool IsNaN(FixedQ4816 value) => false;
    public static bool IsNegative(FixedQ4816 value) => (value.Value < 0L);
    public static bool IsNegativeInfinity(FixedQ4816 value) => false;
    public static bool IsNormal(FixedQ4816 value) => (value.Value != 0L);
    public static bool IsOddInteger(FixedQ4816 value) =>
        ((value.Value & ((1L << FractionBitCount) - 1L)) == 0L) &&
        (((value.Value >> FractionBitCount) & 1L) != 0L);
    public static bool IsPositive(FixedQ4816 value) => (value.Value >= 0L);
    public static bool IsPositiveInfinity(FixedQ4816 value) => false;
    public static bool IsRealNumber(FixedQ4816 value) => true;
    public static bool IsSubnormal(FixedQ4816 value) => false;
    public static bool IsZero(FixedQ4816 value) => (value.Value == 0L);

    public static FixedQ4816 MaxMagnitude(FixedQ4816 x, FixedQ4816 y) {
        var xMagnitude = RawMagnitude(value: x.Value);
        var yMagnitude = RawMagnitude(value: y.Value);

        return ((xMagnitude > yMagnitude) || ((xMagnitude == yMagnitude) && (x.Value >= 0L)) ? x : y);
    }

    public static FixedQ4816 MaxMagnitudeNumber(FixedQ4816 x, FixedQ4816 y) => MaxMagnitude(x: x, y: y);

    public static FixedQ4816 MinMagnitude(FixedQ4816 x, FixedQ4816 y) {
        var xMagnitude = RawMagnitude(value: x.Value);
        var yMagnitude = RawMagnitude(value: y.Value);

        return ((xMagnitude < yMagnitude) || ((xMagnitude == yMagnitude) && (x.Value < 0L)) ? x : y);
    }

    public static FixedQ4816 MinMagnitudeNumber(FixedQ4816 x, FixedQ4816 y) => MinMagnitude(x: x, y: y);

    public static FixedQ4816 Parse(string s, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(s: s.AsSpan(), style: DefaultParseStyle, provider: provider);
    }

    public static FixedQ4816 Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        Parse(s: s, style: DefaultParseStyle, provider: provider);

    public static FixedQ4816 Parse(string s, NumberStyles style, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(s: s.AsSpan(), style: style, provider: provider);
    }

    public static FixedQ4816 Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) {
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
            throw new OverflowException(message: $"Value is outside the representable {nameof(FixedQ4816)} range.");
        }

        _ = decimal.Parse(s: s, style: style, provider: (provider ?? CultureInfo.InvariantCulture));

        throw new FormatException(message: $"The input span was not in a valid {nameof(FixedQ4816)} format.");
    }

    public static bool TryParse(string? s, IFormatProvider? provider, out FixedQ4816 result) =>
        TryParse(s: s, style: DefaultParseStyle, provider: provider, result: out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out FixedQ4816 result) =>
        TryParse(s: s, style: DefaultParseStyle, provider: provider, result: out result);

    public static bool TryParse(string? s, NumberStyles style, IFormatProvider? provider, out FixedQ4816 result) {
        if (s is null) {
            result = default;

            return false;
        }

        return TryParse(s: s.AsSpan(), style: style, provider: provider, result: out result);
    }

    public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out FixedQ4816 result) {
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
        out FixedQ4816 result
    ) {
        result = default;
        var status = FixedPointText.Parse(
            s: s,
            style: style,
            provider: provider,
            fractionBitCount: FractionBitCount,
            parsingDenominator: ParsingDenominator,
            maximumPositiveRaw: long.MaxValue,
            maximumNegativeMagnitudeRaw: (1UL << 63),
            rejectExactOutOfRange: false,
            negative: out var negative,
            rawMagnitude: out var rawMagnitude
        );

        if (FixedPointParseStatus.Success == status) {
            result = new(Value: (negative
                ? ((rawMagnitude == (1UL << 63)) ? long.MinValue : -((long)rawMagnitude))
                : ((long)rawMagnitude)));
        }

        return status;
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) {
        ValidateGeneralFormat(format: format);

        var separator = (provider is null
            ? NumberFormatInfo.InvariantInfo.NumberDecimalSeparator
            : NumberFormatInfo.GetInstance(formatProvider: provider).NumberDecimalSeparator);

        if (separator == ".") {
            return TryFormatCore(destination: destination, charsWritten: out charsWritten);
        }

        Span<char> invariant = stackalloc char[MaximumFormattedLength];
        _ = TryFormatCore(destination: invariant, charsWritten: out var invariantLength);
        var pointIndex = invariant[..invariantLength].IndexOf(value: '.');

        if (pointIndex < 0) {
            if (destination.Length < invariantLength) {
                charsWritten = 0;
                return false;
            }

            invariant[..invariantLength].CopyTo(destination: destination);
            charsWritten = invariantLength;
            return true;
        }

        var requiredLength = (invariantLength - 1 + separator.Length);

        if (destination.Length < requiredLength) {
            charsWritten = 0;
            return false;
        }

        invariant[..pointIndex].CopyTo(destination: destination);
        separator.AsSpan().CopyTo(destination: destination[pointIndex..]);
        invariant[(pointIndex + 1)..invariantLength].CopyTo(destination: destination[(pointIndex + separator.Length)..]);
        charsWritten = requiredLength;
        return true;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) {
        ValidateGeneralFormat(format: format.AsSpan());

        var invariant = ToString();
        var separator = (formatProvider is null
            ? NumberFormatInfo.InvariantInfo.NumberDecimalSeparator
            : NumberFormatInfo.GetInstance(formatProvider: formatProvider).NumberDecimalSeparator);

        return (separator == "."
            ? invariant
            : invariant.Replace(oldValue: ".", newValue: separator, comparisonType: StringComparison.Ordinal));
    }

    private static void ValidateGeneralFormat(ReadOnlySpan<char> format) {
        if (!format.IsEmpty && !format.Equals(other: "G", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            throw new FormatException($"The '{format.ToString()}' format is not supported. Use 'G' for the exact decimal expansion.");
        }
    }

    static bool INumberBase<FixedQ4816>.TryConvertFromChecked<TOther>(TOther value, out FixedQ4816 result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            var other = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            result = new(Value: checked((long)other.Value));

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

    static bool INumberBase<FixedQ4816>.TryConvertFromSaturating<TOther>(TOther value, out FixedQ4816 result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            var other = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            result = new(Value: ((other.Value > long.MaxValue) ? long.MaxValue : ((long)other.Value)));

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

    static bool INumberBase<FixedQ4816>.TryConvertFromTruncating<TOther>(TOther value, out FixedQ4816 result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            var other = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            result = new(Value: ((other.Value > long.MaxValue) ? long.MaxValue : ((long)other.Value)));

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

    static bool INumberBase<FixedQ4816>.TryConvertToChecked<TOther>(FixedQ4816 value, out TOther result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<FixedQ4816, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            var converted = new UFixedQ4816(Value: checked((ulong)value.Value));

            result = Unsafe.As<UFixedQ4816, TOther>(source: ref converted);

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

    static bool INumberBase<FixedQ4816>.TryConvertToSaturating<TOther>(FixedQ4816 value, out TOther result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<FixedQ4816, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            var converted = new UFixedQ4816(Value: ((value.Value < 0L) ? 0UL : ((ulong)value.Value)));

            result = Unsafe.As<UFixedQ4816, TOther>(source: ref converted);

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

    static bool INumberBase<FixedQ4816>.TryConvertToTruncating<TOther>(FixedQ4816 value, out TOther result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<FixedQ4816, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            var converted = new UFixedQ4816(Value: ((value.Value < 0L) ? 0UL : ((ulong)value.Value)));

            result = Unsafe.As<UFixedQ4816, TOther>(source: ref converted);

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

    private static FixedQ4816 FromDecimalChecked(decimal value) {
        var scaled = decimal.Round(d: checked(value * (1L << FractionBitCount)), decimals: 0, mode: MidpointRounding.ToEven);

        return new(Value: checked((long)scaled));
    }

    private static FixedQ4816 FromDoubleChecked(double value) {
        var scaled = double.Round(x: (value * (1L << FractionBitCount)), mode: MidpointRounding.ToEven);

        if (double.IsNaN(d: scaled) || (scaled < ScaledMinimum) || (scaled > ScaledMaximum)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(FixedQ4816)} range.");
        }

        return new(Value: ((scaled <= ScaledMinimum) ? long.MinValue : ((long)scaled)));
    }

    private static FixedQ4816 FromDecimalSaturating(decimal value) {
        decimal scaled;

        try {
            scaled = decimal.Round(d: checked(value * (1L << FractionBitCount)), decimals: 0, mode: MidpointRounding.ToEven);
        } catch (OverflowException) {
            return ((value < 0m) ? MinValue : MaxValue);
        }

        if (scaled <= long.MinValue) { return MinValue; }
        if (scaled >= long.MaxValue) { return MaxValue; }

        return new(Value: ((long)scaled));
    }

    private static ulong RawMagnitude(long value) {
        var sign = (value >> 63);

        return unchecked((ulong)((value ^ sign) - sign));
    }

    private static decimal ToDecimal(FixedQ4816 value) => (value.Value / ((decimal)(1L << FractionBitCount)));

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

    private bool TryFormatCore(Span<char> destination, out int charsWritten) {
        var negative = (Value < 0L);
        var magnitude = RawMagnitude(value: Value);
        var position = 0;

        if (negative) {
            if (destination.IsEmpty) {
                charsWritten = 0;

                return false;
            }

            destination[position++] = '-';
        }

        if (!(magnitude >> FractionBitCount).TryFormat(
            destination: destination[position..],
            charsWritten: out var integerChars,
            format: default,
            provider: CultureInfo.InvariantCulture
        )) {
            charsWritten = 0;

            return false;
        }

        position += integerChars;
        var fraction = (magnitude & ((1UL << FractionBitCount) - 1UL));

        if (fraction == 0UL) {
            charsWritten = position;

            return true;
        }

        if (position >= destination.Length) {
            charsWritten = 0;

            return false;
        }

        destination[position++] = '.';

        do {
            if (position >= destination.Length) {
                charsWritten = 0;

                return false;
            }

            fraction *= 10UL;
            destination[position++] = ((char)('0' + ((int)(fraction >> FractionBitCount))));
            fraction &= ((1UL << FractionBitCount) - 1UL);
        } while (fraction != 0UL);

        charsWritten = position;

        return true;
    }
}
