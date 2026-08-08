using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

public readonly partial record struct FixedQ4816 {
    private const NumberStyles DefaultParseStyle = NumberStyles.AllowLeadingWhite |
                                                     NumberStyles.AllowTrailingWhite |
                                                     NumberStyles.AllowLeadingSign |
                                                     NumberStyles.AllowDecimalPoint;
    private const int MaximumFormattedLength = 34;

    private static readonly UInt128 ParsingDenominator = FixedPointText.CreateParsingDenominator(
        fractionBitCount: FractionBitCount
    );

    public static int Radix => 2;

    public static FixedQ4816 operator +(FixedQ4816 value) => value;

    public static bool IsCanonical(FixedQ4816 value) => true;
    public static bool IsComplexNumber(FixedQ4816 value) => false;
    public static bool IsEvenInteger(FixedQ4816 value) =>
        (((value.Value & ((1L << FractionBitCount) - 1L)) == 0L) &&
        (((value.Value >> FractionBitCount) & 1L) == 0L));
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
        (((value.Value & ((1L << FractionBitCount) - 1L)) == 0L) &&
        (((value.Value >> FractionBitCount) & 1L) != 0L));
    public static bool IsPositive(FixedQ4816 value) => (value.Value >= 0L);
    public static bool IsPositiveInfinity(FixedQ4816 value) => false;
    public static bool IsRealNumber(FixedQ4816 value) => true;
    public static bool IsSubnormal(FixedQ4816 value) => false;
    public static bool IsZero(FixedQ4816 value) => (value.Value == 0L);
    public static FixedQ4816 MaxMagnitude(FixedQ4816 x, FixedQ4816 y) {
        var xMagnitude = FusedArithmetic.RawMagnitude(value: x.Value);
        var yMagnitude = FusedArithmetic.RawMagnitude(value: y.Value);

        return (((xMagnitude > yMagnitude) || ((xMagnitude == yMagnitude) && (x.Value >= 0L))) ? x : y);
    }
    public static FixedQ4816 MaxMagnitudeNumber(FixedQ4816 x, FixedQ4816 y) => MaxMagnitude(x: x, y: y);
    public static FixedQ4816 MinMagnitude(FixedQ4816 x, FixedQ4816 y) {
        var xMagnitude = FusedArithmetic.RawMagnitude(value: x.Value);
        var yMagnitude = FusedArithmetic.RawMagnitude(value: y.Value);

        return (((xMagnitude < yMagnitude) || ((xMagnitude == yMagnitude) && (x.Value < 0L))) ? x : y);
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
    public static bool TryParse(string? s, NumberStyles style, IFormatProvider? provider, out FixedQ4816 result) =>
        // A null string forwards as the empty span rather than short-circuiting, so an invalid style surfaces its
        // ArgumentException — argument validation before data, matching the platform's numeric parsers — while a
        // valid style still answers false with the default left behind.
        TryParse(s: s.AsSpan(), style: style, provider: provider, result: out result);
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

    /// <summary>Renders the exact decimal expansion into <paramref name="destination"/>.</summary>
    /// <param name="destination">The span the rendering is written to.</param>
    /// <param name="charsWritten">The characters written, which is zero whenever this call returns
    /// <see langword="false"/> — the write is all-or-nothing and never leaves a partial rendering behind.</param>
    /// <param name="format">An empty format, <c>G</c> or <c>g</c>; any other specifier raises a
    /// <see cref="FormatException"/>.</param>
    /// <param name="provider">The provider whose <see cref="NumberFormatInfo.NumberDecimalSeparator"/> and
    /// <see cref="NumberFormatInfo.NegativeSign"/> the rendering adopts. Both are spliced into the invariant
    /// expansion rather than re-rendered, and either may be several characters wide, so the required length is
    /// computed from the tokens' own widths rather than from the invariant length.</param>
    /// <returns>Whether the rendering fit.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) {
        FixedPointText.ValidateGeneralFormat(format: format);

        var numberFormat = ((provider is null)
            ? NumberFormatInfo.InvariantInfo
            : NumberFormatInfo.GetInstance(formatProvider: provider));
        var separator = numberFormat.NumberDecimalSeparator;
        var negativeSign = numberFormat.NegativeSign;

        if ((separator == ".") && (negativeSign == "-")) {
            return TryFormatCore(destination: destination, charsWritten: out charsWritten);
        }

        Span<char> invariant = stackalloc char[MaximumFormattedLength];

        _ = TryFormatCore(destination: invariant, charsWritten: out var invariantLength);
        var body = invariant[..invariantLength];
        var negative = (body[0] == '-');

        if (negative) {
            body = body[1..];
        }

        var pointIndex = body.IndexOf(value: '.');
        var signLength = (negative ? negativeSign.Length : 0);
        var requiredLength = ((signLength + body.Length) + ((pointIndex < 0) ? 0 : (separator.Length - 1)));

        if (destination.Length < requiredLength) {
            charsWritten = 0;

            return false;
        }

        if (negative) {
            negativeSign.AsSpan().CopyTo(destination: destination);
        }

        if (pointIndex < 0) {
            body.CopyTo(destination: destination[signLength..]);
        } else {
            body[..pointIndex].CopyTo(destination: destination[signLength..]);
            separator.AsSpan().CopyTo(destination: destination[(signLength + pointIndex)..]);
            body[(pointIndex + 1)..].CopyTo(destination: destination[((signLength + pointIndex) + separator.Length)..]);
        }

        charsWritten = requiredLength;

        return true;
    }

    /// <summary>Renders the exact decimal expansion as a string.</summary>
    /// <param name="format">An empty format, <c>G</c> or <c>g</c>; any other specifier raises a
    /// <see cref="FormatException"/>.</param>
    /// <param name="formatProvider">The provider whose <see cref="NumberFormatInfo.NumberDecimalSeparator"/> and
    /// <see cref="NumberFormatInfo.NegativeSign"/> the rendering adopts, spliced into the invariant expansion. The
    /// separator is substituted first, so a sign token that itself contains a period cannot be re-substituted.</param>
    /// <returns>The rendering, character for character what <see cref="TryFormat"/> writes.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider) {
        FixedPointText.ValidateGeneralFormat(format: format.AsSpan());

        var invariant = ToString();
        var numberFormat = ((formatProvider is null)
            ? NumberFormatInfo.InvariantInfo
            : NumberFormatInfo.GetInstance(formatProvider: formatProvider));
        var separator = numberFormat.NumberDecimalSeparator;
        var negativeSign = numberFormat.NegativeSign;

        if (separator != ".") {
            invariant = invariant.Replace(oldValue: ".", newValue: separator, comparisonType: StringComparison.Ordinal);
        }

        return (((negativeSign == "-") || !invariant.StartsWith(value: '-'))
            ? invariant
            : string.Concat(str0: negativeSign, str1: invariant.AsSpan(start: 1)));
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
    static bool INumberBase<FixedQ4816>.TryConvertFromTruncating<TOther>(TOther value, out FixedQ4816 result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            // The peer carries the SAME width and the SAME Q16 scale, so truncation across signedness is the low
            // sixty-four bits verbatim — the clamp the saturating hook applies belongs to that hook alone.
            var other = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            result = new(Value: unchecked((long)other.Value));

            return true;
        }

        if (FixedPointConvert.TryGetFloating(value: value, result: out var floating)) {
            result = FromDouble(value: floating);

            return true;
        }

        if (FixedPointConvert.TryScaleTruncating(value: value, fractionBitCount: FractionBitCount, out var scaled)) {
            result = new(Value: unchecked((long)scaled));

            return true;
        }

        result = default;

        return false;
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
    static bool INumberBase<FixedQ4816>.TryConvertToTruncating<TOther>(FixedQ4816 value, out TOther result) {
        if (typeof(TOther) == typeof(FixedQ4816)) {
            result = Unsafe.As<FixedQ4816, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(UFixedQ4816)) {
            // Same width, same scale: the low sixty-four bits verbatim, mirroring the from-hook so the two agree.
            var converted = new UFixedQ4816(Value: unchecked((ulong)value.Value));

            result = Unsafe.As<UFixedQ4816, TOther>(source: ref converted);

            return true;
        }

        if (FixedPointConvert.IsKnownBclInteger<TOther>()) {
            // The integer part toward zero, handed to the TARGET's own truncation. Routing it through decimal would
            // let decimal saturate the range instead — byte would answer 255 for three hundred rather than 44.
            result = TOther.CreateTruncating(value: ((Int128)(value.Value / (1L << FractionBitCount))));

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

    private static FixedQ4816 FromDecimalChecked(decimal value) {
        // The exact single ties-to-even rounding of the decimal's own rational; a decimal multiply would round the
        // rescale first and could resolve a manufactured tie off the true value's side.
        var scaled = FixedPointConvert.ScaleDecimal(value: value, fractionBitCount: FractionBitCount);

        if ((scaled < long.MinValue) || (scaled > long.MaxValue)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(FixedQ4816)} range.");
        }

        return new(Value: ((long)scaled));
    }
    private static FixedQ4816 FromDoubleChecked(double value) {
        var scaled = double.Round(x: (value * (1L << FractionBitCount)), mode: MidpointRounding.ToEven);

        if (double.IsNaN(d: scaled) || (scaled < ScaledMinimum) || (scaled > ScaledMaximum)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(FixedQ4816)} range.");
        }

        return new(Value: ((scaled <= ScaledMinimum) ? long.MinValue : ((long)scaled)));
    }
    private static FixedQ4816 FromDecimalSaturating(decimal value) {
        var scaled = FixedPointConvert.ScaleDecimal(value: value, fractionBitCount: FractionBitCount);

        if (scaled < long.MinValue) { return MinValue; }
        if (scaled > long.MaxValue) { return MaxValue; }

        return new(Value: ((long)scaled));
    }
    private static decimal ToDecimal(FixedQ4816 value) => (value.Value / ((decimal)(1L << FractionBitCount)));
    private static bool TrySetFloating<TOther>(FixedQ4816 value, out TOther result)
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
    private bool TryFormatCore(Span<char> destination, out int charsWritten) {
        var negative = (Value < 0L);
        var magnitude = FusedArithmetic.RawMagnitude(value: Value);
        var integerPart = (magnitude >> FractionBitCount);
        var fraction = magnitude & ((1UL << FractionBitCount) - 1UL);
        // The exact required length is a pure function of the raw, so the length check runs BEFORE any write and
        // the caller's destination is genuinely all-or-nothing. Each rendered fraction digit multiplies by ten,
        // which strips exactly one factor of two from the sixteen the denominator holds, so the expansion
        // terminates after sixteen minus the fraction's trailing zero count digits.
        var requiredLength = ((negative ? 1 : 0) + ((int)integerPart.LogarithmBase10()));

        if (fraction != 0UL) {
            requiredLength += (1 + (FractionBitCount - BitOperations.TrailingZeroCount(value: fraction)));
        }

        if (destination.Length < requiredLength) {
            charsWritten = 0;

            return false;
        }

        var position = 0;

        if (negative) {
            destination[position++] = '-';
        }

        _ = integerPart.TryFormat(
            destination: destination[position..],
            charsWritten: out var integerChars,
            format: default,
            provider: CultureInfo.InvariantCulture
        );
        position += integerChars;

        if (fraction != 0UL) {
            // The length check above already reserved the point and every digit, so the write cannot come up short.
            position += FixedPointText.WriteFractionDigits(
                fraction: fraction,
                fractionBitCount: FractionBitCount,
                destination: destination[position..]
            );
        }

        charsWritten = position;

        return true;
    }
}
