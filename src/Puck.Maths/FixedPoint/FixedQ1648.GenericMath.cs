using System.Globalization;
using System.Numerics;

namespace Puck.Maths;

public readonly partial record struct FixedQ1648 {
    private static readonly UInt128 ParsingDenominator = FixedPointText.CreateParsingDenominator(fractionBitCount: FractionBitCount);

    public static int Radix => 2;

    public static FixedQ1648 operator +(FixedQ1648 value) => value;

    public static bool IsCanonical(FixedQ1648 value) => true;
    public static bool IsComplexNumber(FixedQ1648 value) => false;
    public static bool IsEvenInteger(FixedQ1648 value) =>
        (IsInteger(value: value) && (((value.Value >> FractionBitCount) & 1L) == 0L));
    public static bool IsFinite(FixedQ1648 value) => true;
    public static bool IsImaginaryNumber(FixedQ1648 value) => false;
    public static bool IsInfinity(FixedQ1648 value) => false;
    public static bool IsInteger(FixedQ1648 value) =>
        ((value.Value & ((1L << FractionBitCount) - 1L)) == 0L);
    public static bool IsNaN(FixedQ1648 value) => false;
    public static bool IsNegative(FixedQ1648 value) => (value.Value < 0L);
    public static bool IsNegativeInfinity(FixedQ1648 value) => false;
    public static bool IsNormal(FixedQ1648 value) => (value.Value != 0L);
    public static bool IsOddInteger(FixedQ1648 value) =>
        (IsInteger(value: value) && (((value.Value >> FractionBitCount) & 1L) != 0L));
    public static bool IsPositive(FixedQ1648 value) => (value.Value >= 0L);
    public static bool IsPositiveInfinity(FixedQ1648 value) => false;
    public static bool IsRealNumber(FixedQ1648 value) => true;
    public static bool IsSubnormal(FixedQ1648 value) => false;
    public static bool IsZero(FixedQ1648 value) => (value.Value == 0L);
    public static FixedQ1648 MaxMagnitude(FixedQ1648 x, FixedQ1648 y) =>
        new(Value: SignedFixedPointArithmetic.MaximumMagnitude(
            x: x.Value,
            y: y.Value
        ));
    public static FixedQ1648 MaxMagnitudeNumber(FixedQ1648 x, FixedQ1648 y) => MaxMagnitude(
        x: x,
        y: y
    );
    public static FixedQ1648 MinMagnitude(FixedQ1648 x, FixedQ1648 y) =>
        new(Value: SignedFixedPointArithmetic.MinimumMagnitude(
            x: x.Value,
            y: y.Value
        ));
    public static FixedQ1648 MinMagnitudeNumber(FixedQ1648 x, FixedQ1648 y) => MinMagnitude(
        x: x,
        y: y
    );
    public static FixedQ1648 Parse(string s, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(
            s: s.AsSpan(),
            style: FixedPointText.DefaultParseStyle,
            provider: provider
        );
    }
    public static FixedQ1648 Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        Parse(
            provider: provider,
            s: s,
            style: FixedPointText.DefaultParseStyle
        );
    public static FixedQ1648 Parse(string s, NumberStyles style, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(
            s: s.AsSpan(),
            style: style,
            provider: provider
        );
    }
    public static FixedQ1648 Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) =>
        new(Value: FixedPointText.ParseSignedRaw<FixedQ1648>(
            fractionBitCount: FractionBitCount,
            parsingDenominator: ParsingDenominator,
            provider: provider,
            s: s,
            style: style
        ));
    public static bool TryParse(string? s, IFormatProvider? provider, out FixedQ1648 result) =>
        TryParse(
            provider: provider,
            result: out result,
            s: s,
            style: FixedPointText.DefaultParseStyle
        );
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out FixedQ1648 result) =>
        TryParse(
            provider: provider,
            result: out result,
            s: s,
            style: FixedPointText.DefaultParseStyle
        );
    public static bool TryParse(string? s, NumberStyles style, IFormatProvider? provider, out FixedQ1648 result) =>
        // A null string forwards as the empty span rather than short-circuiting, so an invalid style surfaces its
        // ArgumentException — argument validation before data, matching the platform's numeric parsers — while a
        // valid style still answers false with the default left behind.
        TryParse(
            s: s.AsSpan(),
            style: style,
            provider: provider,
            result: out result
        );
    public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out FixedQ1648 result) {
        var parsed = FixedPointText.TryParseSignedRaw(
            fractionBitCount: FractionBitCount,
            parsingDenominator: ParsingDenominator,
            provider: provider,
            rawValue: out var rawValue,
            s: s,
            style: style
        );

        result = new(Value: rawValue);

        return parsed;
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
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        FixedPointText.TryFormatSignedGeneral(
            charsWritten: out charsWritten,
            destination: destination,
            format: format,
            fractionBitCount: FractionBitCount,
            provider: provider,
            rawValue: Value
        );
    /// <summary>Renders the exact decimal expansion as a string.</summary>
    /// <param name="format">An empty format, <c>G</c> or <c>g</c>; any other specifier raises a
    /// <see cref="FormatException"/>.</param>
    /// <param name="formatProvider">The provider whose <see cref="NumberFormatInfo.NumberDecimalSeparator"/> and
    /// <see cref="NumberFormatInfo.NegativeSign"/> the rendering adopts, spliced into the invariant expansion. The
    /// separator is substituted first, so a sign token that itself contains a period cannot be re-substituted.</param>
    /// <returns>The rendering, character for character what <see cref="TryFormat"/> writes.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        FixedPointText.SpliceGeneralFormat(
            format: format,
            invariant: ToString(),
            provider: formatProvider
        );

    static bool INumberBase<FixedQ1648>.TryConvertFromChecked<TOther>(TOther value, out FixedQ1648 result) =>
        FixedPointConvert.TryConvertFromChecked<FixedQ1648, TOther>(
            fractionBitCount: FractionBitCount,
            result: out result,
            value: value
        );
    static bool INumberBase<FixedQ1648>.TryConvertFromSaturating<TOther>(TOther value, out FixedQ1648 result) =>
        FixedPointConvert.TryConvertFromSaturating<FixedQ1648, TOther>(
            fractionBitCount: FractionBitCount,
            result: out result,
            value: value
        );
    static bool INumberBase<FixedQ1648>.TryConvertFromTruncating<TOther>(TOther value, out FixedQ1648 result) =>
        FixedPointConvert.TryConvertFromTruncating<FixedQ1648, TOther>(
            fractionBitCount: FractionBitCount,
            result: out result,
            value: value
        );
    static bool INumberBase<FixedQ1648>.TryConvertToChecked<TOther>(FixedQ1648 value, out TOther result) =>
        FixedPointConvert.TryConvertToChecked<FixedQ1648, TOther>(
            fractionBitCount: FractionBitCount,
            result: out result,
            value: value
        );
    static bool INumberBase<FixedQ1648>.TryConvertToSaturating<TOther>(FixedQ1648 value, out TOther result) =>
        FixedPointConvert.TryConvertToSaturating<FixedQ1648, TOther>(
            fractionBitCount: FractionBitCount,
            result: out result,
            value: value
        );
    static bool INumberBase<FixedQ1648>.TryConvertToTruncating<TOther>(FixedQ1648 value, out TOther result) =>
        FixedPointConvert.TryConvertToTruncating<FixedQ1648, TOther>(
            fractionBitCount: FractionBitCount,
            result: out result,
            value: value
        );
}
