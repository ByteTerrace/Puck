using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

public readonly partial record struct FixedQ3232 {
    private const NumberStyles DefaultParseStyle = NumberStyles.AllowLeadingWhite |
                                                     NumberStyles.AllowTrailingWhite |
                                                     NumberStyles.AllowLeadingSign |
                                                     NumberStyles.AllowDecimalPoint;

    private static readonly UInt128 ParsingDenominator = FixedPointText.CreateParsingDenominator(
        fractionBitCount: FractionBitCount
    );

    public static int Radix => 2;

    public static FixedQ3232 operator +(FixedQ3232 value) => value;

    public static bool IsCanonical(FixedQ3232 value) => true;
    public static bool IsComplexNumber(FixedQ3232 value) => false;
    public static bool IsEvenInteger(FixedQ3232 value) =>
        (((value.Value & ((1L << FractionBitCount) - 1L)) == 0L) &&
        (((value.Value >> FractionBitCount) & 1L) == 0L));
    public static bool IsFinite(FixedQ3232 value) => true;
    public static bool IsImaginaryNumber(FixedQ3232 value) => false;
    public static bool IsInfinity(FixedQ3232 value) => false;
    public static bool IsInteger(FixedQ3232 value) =>
        ((value.Value & ((1L << FractionBitCount) - 1L)) == 0L);
    public static bool IsNaN(FixedQ3232 value) => false;
    public static bool IsNegative(FixedQ3232 value) => (value.Value < 0L);
    public static bool IsNegativeInfinity(FixedQ3232 value) => false;
    public static bool IsNormal(FixedQ3232 value) => (value.Value != 0L);
    public static bool IsOddInteger(FixedQ3232 value) =>
        (((value.Value & ((1L << FractionBitCount) - 1L)) == 0L) &&
        (((value.Value >> FractionBitCount) & 1L) != 0L));
    public static bool IsPositive(FixedQ3232 value) => (value.Value >= 0L);
    public static bool IsPositiveInfinity(FixedQ3232 value) => false;
    public static bool IsRealNumber(FixedQ3232 value) => true;
    public static bool IsSubnormal(FixedQ3232 value) => false;
    public static bool IsZero(FixedQ3232 value) => (value.Value == 0L);
    public static FixedQ3232 MaxMagnitude(FixedQ3232 x, FixedQ3232 y) {
        var xMagnitude = FusedArithmetic.RawMagnitude(value: x.Value);
        var yMagnitude = FusedArithmetic.RawMagnitude(value: y.Value);

        return (((xMagnitude > yMagnitude) || ((xMagnitude == yMagnitude) && (x.Value >= 0L))) ? x : y);
    }
    public static FixedQ3232 MaxMagnitudeNumber(FixedQ3232 x, FixedQ3232 y) => MaxMagnitude(x: x, y: y);
    public static FixedQ3232 MinMagnitude(FixedQ3232 x, FixedQ3232 y) {
        var xMagnitude = FusedArithmetic.RawMagnitude(value: x.Value);
        var yMagnitude = FusedArithmetic.RawMagnitude(value: y.Value);

        return (((xMagnitude < yMagnitude) || ((xMagnitude == yMagnitude) && (x.Value < 0L))) ? x : y);
    }
    public static FixedQ3232 MinMagnitudeNumber(FixedQ3232 x, FixedQ3232 y) => MinMagnitude(x: x, y: y);
    public static FixedQ3232 Parse(string s, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(s: s.AsSpan(), style: DefaultParseStyle, provider: provider);
    }
    public static FixedQ3232 Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        Parse(s: s, style: DefaultParseStyle, provider: provider);
    public static FixedQ3232 Parse(string s, NumberStyles style, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(s: s.AsSpan(), style: style, provider: provider);
    }
    public static FixedQ3232 Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) {
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
            throw new OverflowException(message: $"Value is outside the representable {nameof(FixedQ3232)} range.");
        }

        _ = decimal.Parse(s: s, style: style, provider: (provider ?? CultureInfo.InvariantCulture));

        throw new FormatException(message: $"The input span was not in a valid {nameof(FixedQ3232)} format.");
    }
    public static bool TryParse(string? s, IFormatProvider? provider, out FixedQ3232 result) =>
        TryParse(s: s, style: DefaultParseStyle, provider: provider, result: out result);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out FixedQ3232 result) =>
        TryParse(s: s, style: DefaultParseStyle, provider: provider, result: out result);
    public static bool TryParse(string? s, NumberStyles style, IFormatProvider? provider, out FixedQ3232 result) =>
        TryParse(s: s.AsSpan(), style: style, provider: provider, result: out result);
    public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out FixedQ3232 result) {
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
        out FixedQ3232 result
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

        return FixedPointText.TryFormatSigned(
            rawValue: Value,
            fractionBitCount: FractionBitCount,
            destination: destination,
            charsWritten: out charsWritten,
            provider: provider
        );
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

        return FixedPointText.SpliceProviderTokens(invariant: ToString(), provider: formatProvider);
    }

    static bool INumberBase<FixedQ3232>.TryConvertFromChecked<TOther>(TOther value, out FixedQ3232 result) {
        if (typeof(TOther) == typeof(FixedQ3232)) {
            result = Unsafe.As<TOther, FixedQ3232>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);

            result = FromFixedQ4816(value: other);

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
    static bool INumberBase<FixedQ3232>.TryConvertFromSaturating<TOther>(TOther value, out FixedQ3232 result) {
        if (typeof(TOther) == typeof(FixedQ3232)) {
            result = Unsafe.As<TOther, FixedQ3232>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);
            var widened = (((Int128)other.Value) << PeerNarrowShift);

            result = ((widened < long.MinValue)
                ? MinValue
                : ((widened > long.MaxValue)
                    ? MaxValue
                    : new(Value: ((long)widened))));

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
    static bool INumberBase<FixedQ3232>.TryConvertFromTruncating<TOther>(TOther value, out FixedQ3232 result) {
        if (typeof(TOther) == typeof(FixedQ3232)) {
            result = Unsafe.As<TOther, FixedQ3232>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            // Width truncation, not range clamping: the exact widened value's low sixty-four bits, mirroring the
            // pattern the peer-carrier hooks use elsewhere in this folder.
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);
            var widened = (((Int128)other.Value) << PeerNarrowShift);

            result = new(Value: unchecked((long)widened));

            return true;
        }

        if (FixedPointConvert.TryGetFloating(value: value, result: out var floating)) {
            result = FromDouble(value: floating);

            return true;
        }

        if (typeof(TOther) == typeof(decimal)) {
            result = FromDecimalTruncating(value: Unsafe.As<TOther, decimal>(source: ref value));

            return true;
        }

        if (FixedPointConvert.TryScaleTruncating(value: value, fractionBitCount: FractionBitCount, out var scaled)) {
            result = new(Value: unchecked((long)scaled));

            return true;
        }

        result = default;

        return false;
    }
    static bool INumberBase<FixedQ3232>.TryConvertToChecked<TOther>(FixedQ3232 value, out TOther result) {
        if (typeof(TOther) == typeof(FixedQ3232)) {
            result = Unsafe.As<FixedQ3232, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var converted = value.ToFixedQ4816();

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
    static bool INumberBase<FixedQ3232>.TryConvertToSaturating<TOther>(FixedQ3232 value, out TOther result) {
        if (typeof(TOther) == typeof(FixedQ3232)) {
            result = Unsafe.As<FixedQ3232, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var converted = value.ToFixedQ4816();

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
    static bool INumberBase<FixedQ3232>.TryConvertToTruncating<TOther>(FixedQ3232 value, out TOther result) {
        if (typeof(TOther) == typeof(FixedQ3232)) {
            result = Unsafe.As<FixedQ3232, TOther>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(FixedQ4816)) {
            var converted = value.ToFixedQ4816();

            result = Unsafe.As<FixedQ4816, TOther>(source: ref converted);

            return true;
        }

        if (FixedPointConvert.IsKnownBclInteger<TOther>()) {
            // The integer part toward zero, handed to the TARGET's own truncation, mirroring FixedQ4816's hook.
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

    // FixedQ3232's own decimal boundary. FixedPointConvert.ScaleDecimal is only exact through thirty-one fraction
    // bits (a UInt128 intermediate); at thirty-two fraction bits this format crosses that cap by one, so it routes
    // through ScaleDecimalWide's BigInteger intermediate instead, which has no such ceiling — the same door
    // FixedQ1648 (forty-eight fraction bits) uses for the same reason. decimal itself tops out at twenty-eight to
    // twenty-nine significant digits, fewer than the up-to-thirty-two-digit exact expansion this format's finest
    // raws can need, so a decimal round trip through these three members is exact wherever decimal's own precision
    // covers the value and is decimal's own nearest representable otherwise — the same boundary decimal draws for
    // every consumer of it, not a limitation this type introduces.
    private static FixedQ3232 FromDecimalChecked(decimal value) {
        var scaled = FixedPointConvert.ScaleDecimalWide(value: value, fractionBitCount: FractionBitCount);

        if ((scaled < long.MinValue) || (scaled > long.MaxValue)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(FixedQ3232)} range.");
        }

        return new(Value: ((long)scaled));
    }
    private static FixedQ3232 FromDecimalSaturating(decimal value) {
        var scaled = FixedPointConvert.ScaleDecimalWide(value: value, fractionBitCount: FractionBitCount);

        if (scaled < long.MinValue) { return MinValue; }
        if (scaled > long.MaxValue) { return MaxValue; }

        return new(Value: ((long)scaled));
    }
    private static FixedQ3232 FromDecimalTruncating(decimal value) {
        var scaled = FixedPointConvert.ScaleDecimalWide(value: value, fractionBitCount: FractionBitCount);
        var wrapped = (scaled & ulong.MaxValue); // low sixty-four bits, exact even for a negative BigInteger

        return new(Value: unchecked((long)(ulong)wrapped));
    }
    private static FixedQ3232 FromDoubleChecked(double value) {
        var scaled = double.Round(x: (value * (1L << FractionBitCount)), mode: MidpointRounding.ToEven);

        if (double.IsNaN(d: scaled) || (scaled < ScaledMinimum) || (scaled > ScaledMaximum)) {
            throw new OverflowException(message: $"Value is outside the representable {nameof(FixedQ3232)} range.");
        }

        return new(Value: ((scaled <= ScaledMinimum) ? long.MinValue : ((long)scaled)));
    }
    // decimal's twenty-eight-to-twenty-nine significant digits cannot exactly hold every raw this format can name
    // (the finest ones need up to thirty-two fraction digits), so this bridge rounds to decimal's own nearest
    // representable rather than this type's. FromDouble/FromInteger/FromRawBits and the FixedQ4816 peer conversion
    // remain exact; this member exists only for the generic-math TOther bridge.
    private static decimal ToDecimal(FixedQ3232 value) => (value.Value / ((decimal)(1L << FractionBitCount)));
    private static bool TrySetFloating<TOther>(FixedQ3232 value, out TOther result)
        where TOther : INumberBase<TOther> {
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
