using System.Globalization;
using System.Numerics;

namespace Puck.Maths;

internal enum FixedPointParseStatus {
    Success,
    Invalid,
    Overflow
}
/// <summary>Exact decimal parsing and rendering shared by the fixed-point primitives. Rendering
/// (<see cref="TryFormatRaw"/>) is always allocation-free. <see cref="Parse"/>'s fraction-digit accumulation
/// and rounding stay allocation-free too, in <see cref="UInt128"/>, for every carrier at or below
/// <see cref="NarrowFractionBitCountLimit"/> fraction bits — every format today except one. Only above that limit does
/// the accumulation route through <see cref="BigInteger"/> (and therefore allocate), because a carrier wide enough to
/// need forty-eight or more fraction bits — <c>FixedQ1648</c>'s Q16.48 — reads up to <c>fractionBitCount + 1</c>
/// decimal digits, which can exceed <see cref="UInt128"/>'s width; every value the arithmetic narrows back to is
/// proved to fit its original width, so the wide path is a strict generalization and changes no result versus the
/// narrow one at any fraction width where both could run.</summary>
internal static class FixedPointText {
    /// <summary>The style every fixed-point carrier parses under when the caller names none: leading and trailing
    /// white space, a leading sign, and a decimal point. Thousands separators, an exponent, and a currency symbol are
    /// all admitted only when a caller asks for them by style.</summary>
    internal const NumberStyles DefaultParseStyle = NumberStyles.AllowLeadingWhite |
                                                   NumberStyles.AllowTrailingWhite |
                                                   NumberStyles.AllowLeadingSign |
                                                   NumberStyles.AllowDecimalPoint;

    /// <summary>A buffer length covering the invariant expansion of every 64-bit raw at any fraction bit count: a
    /// sign, twenty integer digits (the decimal length of <c>2⁶⁴</c>), the point, and one fraction digit per fraction
    /// bit.</summary>
    private const int MaximumInvariantLength = (((1 + 20) + 1) + 64);
    /// <summary>The largest fraction bit count whose <c>fractionBitCount + 1</c>-decimal-digit accumulation still fits
    /// <see cref="UInt128"/> (<c>10^38 &lt; 2^128 ≤ 10^39</c>), and therefore the largest <see cref="Parse"/> serves
    /// allocation-free. Every carrier below <c>FixedQ1648</c>'s forty-eight fraction bits — sixteen and thirty-two —
    /// sits under it today.</summary>
    private const int NarrowFractionBitCountLimit = 37;
    private const int StoredSignificantDigitCount = 64;

    /// <summary>
    /// Creates <c>2 × 5^(fractionBitCount + 1)</c>, the reduced denominator obtained when a decimal prefix with
    /// <c>fractionBitCount + 1</c> digits is scaled by <c>2^fractionBitCount</c>.
    /// </summary>
    internal static UInt128 CreateParsingDenominator(int fractionBitCount) {
        var powerOfFive = UInt128.One;

        for (var i = 0; (i <= fractionBitCount); i++) {
            powerOfFive *= 5U;
        }

        return (powerOfFive << 1);
    }
    /// <summary>Renders the invariant exact decimal expansion of a raw fixed-point magnitude as a string.</summary>
    /// <param name="magnitude">The raw magnitude.</param>
    /// <param name="negative">Whether the value is negative.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <returns>The invariant exact decimal expansion.</returns>
    internal static string FormatRaw(ulong magnitude, bool negative, int fractionBitCount) {
        Span<char> buffer = stackalloc char[MaximumInvariantLength];

        _ = TryFormatRaw(
            charsWritten: out var charsWritten,
            destination: buffer,
            fractionBitCount: fractionBitCount,
            magnitude: magnitude,
            negative: negative
        );

        return new string(value: buffer[..charsWritten]);
    }
    /// <summary>Renders the invariant exact decimal expansion of a signed raw value as a string.</summary>
    /// <param name="rawValue">The signed raw value.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <returns>The invariant exact decimal expansion.</returns>
    internal static string FormatSignedRaw(long rawValue, int fractionBitCount) =>
        FormatRaw(
            magnitude: FusedArithmetic.RawMagnitude(value: rawValue),
            negative: (rawValue < 0L),
            fractionBitCount: fractionBitCount
        );
    /// <summary>
    /// Validates the culture/style syntax with the platform number parser, then quantizes the original digits
    /// directly. The intermediate <see cref="decimal"/> value supplies only the sign; its rounded magnitude is never
    /// used.
    /// </summary>
    internal static FixedPointParseStatus Parse(
        ReadOnlySpan<char> s,
        NumberStyles style,
        IFormatProvider? provider,
        int fractionBitCount,
        UInt128 parsingDenominator,
        ulong maximumPositiveRaw,
        ulong maximumNegativeMagnitudeRaw,
        bool rejectExactOutOfRange,
        out bool negative,
        out ulong rawMagnitude
    ) {
        negative = false;
        rawMagnitude = 0UL;

        var effectiveProvider = (provider ?? CultureInfo.InvariantCulture);

        if (!decimal.TryParse(
            provider: effectiveProvider,
            result: out var validated,
            s: s,
            style: style
        )) {
            return FixedPointParseStatus.Invalid;
        }

        negative = (validated < 0m);

        var numberFormat = NumberFormatInfo.GetInstance(formatProvider: effectiveProvider);
        var useCurrencySeparators = UsesCurrencySeparators(
            numberFormat: numberFormat,
            s: s,
            style: style
        );
        ReadOnlySpan<char> decimalSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyDecimalSeparator
            : numberFormat.NumberDecimalSeparator
        );
        ReadOnlySpan<char> groupSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyGroupSeparator
            : numberFormat.NumberGroupSeparator
        );

        // The exact scanner must be able to distinguish syntax tokens from significand digits and the exponent
        // marker. Built-in cultures (including alphabetic currency symbols and multi-character tokens) satisfy that
        // requirement. Separator tokens are handled explicitly during exponent discovery. A hand-built NFI can still
        // make a free-form sign/currency token contain digits; alias its currency symbol with a sign token so that
        // UsesCurrencySeparators reads a plain signed number as currency-formatted; alias a separator with a sign
        // token, which the platform classifies by grammar position and this scanner by string match; hide a
        // separator inside the currency symbol, which the platform consumes whole; spell a decimal separator whose
        // text begins as parser white space, which the platform consumes in its white-space phase; put the exponent
        // marker inside a separator token, which exponent discovery skips before testing for 'e'; or split the
        // number and currency separator families so that the family this scanner did not pick still appears in the
        // input. Every one of those shapes could let the BCL validate one number while the exact pass quantizes
        // another, so each is REFUSED here rather than quantized: a parse that succeeds names the number the
        // validation pass accepted.
        if (HasAmbiguousEnabledFormatToken(
            numberFormat: numberFormat,
            s: s,
            style: style,
            useCurrencySeparators: useCurrencySeparators
        )) {
            return FixedPointParseStatus.Invalid;
        }

        var exponentIndex = FindExponent(
            s: s,
            style: style,
            numberFormat: numberFormat,
            decimalSeparator: decimalSeparator,
            groupSeparator: groupSeparator,
            exponentLimit: ((((long)s.Length) + fractionBitCount) + 21L),
            exponent: out var exponent
        );
        var significand = ((0 <= exponentIndex)
            ? s[..exponentIndex]
            : s
        );
        Span<byte> significantDigits = stackalloc byte[StoredSignificantDigitCount];
        var storedDigitCount = 0;
        var totalDigitCount = 0L;
        var leadingZeroCount = 0L;
        var decimalDigitIndex = -1L;
        var lastNonzeroSignificantIndex = -1L;
        var seenNonzero = false;

        for (var index = 0; (index < significand.Length);) {
            var digit = (significand[index] - '0');

            if (((uint)digit) <= 9U) {
                if (!seenNonzero) {
                    if (0 == digit) {
                        leadingZeroCount++;
                    } else {
                        seenNonzero = true;
                    }
                }

                if (seenNonzero) {
                    var significantIndex = (totalDigitCount - leadingZeroCount);

                    if (storedDigitCount < significantDigits.Length) {
                        significantDigits[storedDigitCount++] = ((byte)digit);
                    }

                    if (0 != digit) {
                        lastNonzeroSignificantIndex = significantIndex;
                    }
                }

                totalDigitCount++;
                index++;

                continue;
            }

            if (
                (0L > decimalDigitIndex) &&
                !decimalSeparator.IsEmpty &&
                significand[index..].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: decimalSeparator
            )
            ) {
                decimalDigitIndex = totalDigitCount;
                index += decimalSeparator.Length;

                continue;
            }

            if (
                !groupSeparator.IsEmpty &&
                significand[index..].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: groupSeparator
            )
            ) {
                index += groupSeparator.Length;

                continue;
            }

            index++;
        }

        if (!seenNonzero) {
            negative = false;

            return FixedPointParseStatus.Success;
        }

        if (0L > decimalDigitIndex) {
            decimalDigitIndex = totalDigitCount;
        }

        var integerSignificantDigitCount = ((decimalDigitIndex + exponent) - leadingZeroCount);

        // Every supported result has at most twenty integer digits. A nonzero significand with more cannot become
        // representable through fractional rounding.
        if (20L < integerSignificantDigitCount) {
            return FixedPointParseStatus.Overflow;
        }

        var integerPart = UInt128.Zero;

        for (var digitIndex = 0L; (digitIndex < integerSignificantDigitCount); digitIndex++) {
            var digit = ((digitIndex < storedDigitCount)
                ? significantDigits[((int)digitIndex)]
                : ((byte)0)
            );

            integerPart = ((integerPart * 10U) + digit);
        }

        var integerRaw = (integerPart << fractionBitCount);
        var fractionDigitLimit = (fractionBitCount + 1);
        var hasNonzeroDiscardedFractionDigit =
            (lastNonzeroSignificantIndex >= (integerSignificantDigitCount + fractionDigitLimit));
        var maximumRaw = (negative
            ? maximumNegativeMagnitudeRaw
            : maximumPositiveRaw
        );
        UInt128 fractionRaw;

        // fractionDigitLimit decimal digits can reach 10^(fractionBitCount + 1) − 1. That fits UInt128
        // (10^38 < 2^128) through NarrowFractionBitCountLimit and no further — FixedQ1648's forty-eight fraction bits
        // needs forty-nine decimal digits, about 163 bits, past UInt128's 128 — so only above the limit does the
        // accumulation and its rounding move to BigInteger (and therefore allocate). Both branches share one formula;
        // only the carrier width differs, and the quotient either narrows to is provably below 2^fractionBitCount
        // (see the remark on the narrow branch's own division), so switching branches at the limit changes no result.
        if (fractionBitCount <= NarrowFractionBitCountLimit) {
            var fractionPrefix = UInt128.Zero;

            for (var fractionIndex = 0; (fractionIndex < fractionDigitLimit); fractionIndex++) {
                var significantIndex = (integerSignificantDigitCount + fractionIndex);
                var digit = (((0L <= significantIndex) && (significantIndex < storedDigitCount))
                    ? significantDigits[((int)significantIndex)]
                    : ((byte)0)
                );

                fractionPrefix = ((fractionPrefix * 10U) + digit);
            }

            if (rejectExactOutOfRange) {
                if (integerRaw > maximumRaw) {
                    return FixedPointParseStatus.Overflow;
                }

                var scaledPrefixNumerator = ((integerRaw * parsingDenominator) + fractionPrefix);
                var maximumNumerator = (((UInt128)maximumRaw) * parsingDenominator);

                if (
                    (scaledPrefixNumerator > maximumNumerator) ||
                    (
                        (scaledPrefixNumerator == maximumNumerator) &&
                        hasNonzeroDiscardedFractionDigit
                    )
                ) {
                    return FixedPointParseStatus.Overflow;
                }
            }

            // fractionPrefix < 10^(fractionBitCount + 1) = 2^(fractionBitCount + 1) · 5^(fractionBitCount + 1), and
            // parsingDenominator = 2 · 5^(fractionBitCount + 1), so the quotient below is strictly below
            // 2^fractionBitCount — every format's own whole-unit raw.
            var narrowFractionRaw = (fractionPrefix / parsingDenominator);
            var narrowRemainder = (fractionPrefix - (narrowFractionRaw * parsingDenominator));
            var narrowHalf = (parsingDenominator >> 1);

            if (
                (narrowRemainder > narrowHalf) ||
                (
                    (narrowRemainder == narrowHalf) &&
                    (
                        hasNonzeroDiscardedFractionDigit ||
                        !UInt128.IsEvenInteger(value: narrowFractionRaw)
                    )
                )
            ) {
                narrowFractionRaw++;
            }

            fractionRaw = narrowFractionRaw;
        } else {
            // A WIDE accumulator: BigInteger carries the accumulation and the rounding exactly past
            // NarrowFractionBitCountLimit — every value it narrows back to is proved to fit its original width (see
            // the remark on the narrow branch above), so this is a strict generalization of that branch, not a
            // second rule.
            var fractionPrefix = BigInteger.Zero;

            for (var fractionIndex = 0; (fractionIndex < fractionDigitLimit); fractionIndex++) {
                var significantIndex = (integerSignificantDigitCount + fractionIndex);
                var digit = (((0L <= significantIndex) && (significantIndex < storedDigitCount))
                    ? significantDigits[((int)significantIndex)]
                    : ((byte)0)
                );

                fractionPrefix = ((fractionPrefix * 10) + digit);
            }

            var wideParsingDenominator = ((BigInteger)parsingDenominator);

            if (rejectExactOutOfRange) {
                if (integerRaw > maximumRaw) {
                    return FixedPointParseStatus.Overflow;
                }

                var scaledPrefixNumerator = ((((BigInteger)integerRaw) * wideParsingDenominator) + fractionPrefix);
                var maximumNumerator = (((BigInteger)maximumRaw) * wideParsingDenominator);

                if (
                    (scaledPrefixNumerator > maximumNumerator) ||
                    (
                        (scaledPrefixNumerator == maximumNumerator) &&
                        hasNonzeroDiscardedFractionDigit
                    )
                ) {
                    return FixedPointParseStatus.Overflow;
                }
            }

            // fractionPrefix < 10^(fractionBitCount + 1) = 2^(fractionBitCount + 1) · 5^(fractionBitCount + 1), and
            // wideParsingDenominator = 2 · 5^(fractionBitCount + 1), so the quotient below is strictly below
            // 2^fractionBitCount — every format's own whole-unit raw — and narrows to UInt128 without loss.
            var fractionRawWide = (fractionPrefix / wideParsingDenominator);
            var remainder = (fractionPrefix - (fractionRawWide * wideParsingDenominator));
            var half = (wideParsingDenominator >> 1);

            if (
                (remainder > half) ||
                (
                    (remainder == half) &&
                    (
                        hasNonzeroDiscardedFractionDigit ||
                        !fractionRawWide.IsEven
                    )
                )
            ) {
                fractionRawWide++;
            }

            fractionRaw = ((UInt128)fractionRawWide);
        }

        var roundedRaw = (integerRaw + fractionRaw);

        if (roundedRaw > maximumRaw) {
            return FixedPointParseStatus.Overflow;
        }

        rawMagnitude = ((ulong)roundedRaw);
        negative &= (0UL != rawMagnitude);

        return FixedPointParseStatus.Success;
    }
    /// <summary>Parses text into the raw of a signed 64-bit fixed-point carrier, throwing the diagnosis that
    /// carrier's public <c>Parse</c> states.</summary>
    /// <typeparam name="TSelf">The carrier the diagnoses name.</typeparam>
    /// <param name="s">The text to parse.</param>
    /// <param name="style">The styles <paramref name="s"/> is admitted under.</param>
    /// <param name="provider">The provider supplying the numeric conventions, or <see langword="null"/> for the
    /// invariant culture.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <param name="parsingDenominator">The carrier's <see cref="CreateParsingDenominator"/> value.</param>
    /// <returns>The parsed raw value.</returns>
    /// <exception cref="OverflowException">The value is outside the carrier's range.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> is not a valid literal.</exception>
    /// <remarks>The platform parser is re-entered only on failure, so this preserves its format-versus-overflow
    /// distinction while a successful value is always quantized from the original digits.</remarks>
    internal static long ParseSignedRaw<TSelf>(
        ReadOnlySpan<char> s,
        NumberStyles style,
        IFormatProvider? provider,
        int fractionBitCount,
        UInt128 parsingDenominator
    ) {
        var status = ParseSignedRawCore(
            fractionBitCount: fractionBitCount,
            parsingDenominator: parsingDenominator,
            provider: provider,
            rawValue: out var rawValue,
            s: s,
            style: style
        );

        if (FixedPointParseStatus.Success == status) {
            return rawValue;
        }

        if (FixedPointParseStatus.Overflow == status) {
            throw new OverflowException(message: $"Value is outside the representable {typeof(TSelf).Name} range.");
        }

        _ = decimal.Parse(
            provider: (provider ?? CultureInfo.InvariantCulture),
            s: s,
            style: style
        );

        throw new FormatException(message: $"The input span was not in a valid {typeof(TSelf).Name} format.");
    }
    /// <summary>Refuses any format specifier other than the exact decimal expansion, then splices a provider's number
    /// tokens into an invariant expansion.</summary>
    /// <param name="invariant">The invariant exact decimal expansion, as the carrier's own
    /// <see cref="object.ToString"/> renders it.</param>
    /// <param name="format">An empty format, <c>G</c> or <c>g</c>.</param>
    /// <param name="provider">The provider whose number tokens are spliced into the invariant expansion.</param>
    /// <returns>The spliced expansion, character for character what <see cref="TryFormat"/> writes.</returns>
    /// <exception cref="FormatException"><paramref name="format"/> is another specifier.</exception>
    internal static string SpliceGeneralFormat(string invariant, string? format, IFormatProvider? provider) {
        ValidateGeneralFormat(format: format.AsSpan());

        return SpliceProviderTokens(
            invariant: invariant,
            provider: provider
        );
    }
    /// <summary>Splices a provider's decimal-separator and negative-sign tokens into an invariant expansion.</summary>
    /// <param name="invariant">The invariant exact decimal expansion.</param>
    /// <param name="provider">The provider whose number tokens are spliced into the invariant expansion.</param>
    /// <returns>The spliced expansion, or <paramref name="invariant"/> itself when no token differs.</returns>
    internal static string SpliceProviderTokens(string invariant, IFormatProvider? provider) {
        var numberFormat = ((provider is null)
            ? NumberFormatInfo.InvariantInfo
            : NumberFormatInfo.GetInstance(formatProvider: provider)
        );
        var negative = invariant.StartsWith(value: '-');
        var pointIndex = invariant.IndexOf(value: '.');

        if (
            (!negative || (numberFormat.NegativeSign == "-")) &&
            ((pointIndex < 0) || (numberFormat.NumberDecimalSeparator == "."))
        ) {
            return invariant;
        }

        var requiredLength = ((invariant.Length
            + (negative
            ? (numberFormat.NegativeSign.Length - 1)
            : 0))
            + ((pointIndex < 0)
            ? 0
            : (numberFormat.NumberDecimalSeparator.Length - 1)));

        return string.Create(
            length: requiredLength,
            state: (invariant, numberFormat),
            action: static (destination, state) => _ = TrySpliceProviderTokens(
                charsWritten: out _,
                destination: destination,
                invariant: state.invariant,
                numberFormat: state.numberFormat
            )
        );
    }
    /// <summary>Writes a raw fixed-point magnitude using a provider's decimal-separator and negative-sign
    /// tokens.</summary>
    /// <param name="magnitude">The raw magnitude.</param>
    /// <param name="negative">Whether the value is negative.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <param name="destination">The span the expansion is written to.</param>
    /// <param name="charsWritten">The characters written, or zero when the destination is too small.</param>
    /// <param name="provider">The provider whose number tokens are spliced into the invariant expansion.</param>
    /// <returns>Whether the complete expansion fit. A refusal leaves <paramref name="destination"/> untouched.</returns>
    internal static bool TryFormat(
        ulong magnitude,
        bool negative,
        int fractionBitCount,
        Span<char> destination,
        out int charsWritten,
        IFormatProvider? provider
    ) {
        var numberFormat = ((provider is null)
            ? NumberFormatInfo.InvariantInfo
            : NumberFormatInfo.GetInstance(formatProvider: provider)
        );

        if (
            (numberFormat.NumberDecimalSeparator == ".") &&
            (!negative || (numberFormat.NegativeSign == "-"))
        ) {
            return TryFormatRaw(
                charsWritten: out charsWritten,
                destination: destination,
                fractionBitCount: fractionBitCount,
                magnitude: magnitude,
                negative: negative
            );
        }

        Span<char> invariant = stackalloc char[MaximumInvariantLength];

        _ = TryFormatRaw(
            charsWritten: out var invariantLength,
            destination: invariant,
            fractionBitCount: fractionBitCount,
            magnitude: magnitude,
            negative: negative
        );

        return TrySpliceProviderTokens(
            invariant: invariant[..invariantLength],
            numberFormat: numberFormat,
            destination: destination,
            charsWritten: out charsWritten
        );
    }
    /// <summary>Writes the invariant exact decimal expansion of a raw fixed-point magnitude.</summary>
    /// <param name="magnitude">The raw magnitude.</param>
    /// <param name="negative">Whether the value is negative.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <param name="destination">The span the expansion is written to.</param>
    /// <param name="charsWritten">The characters written, or zero when the destination is too small.</param>
    /// <returns>Whether the complete expansion fit. A refusal leaves <paramref name="destination"/> untouched.</returns>
    internal static bool TryFormatRaw(
        ulong magnitude,
        bool negative,
        int fractionBitCount,
        Span<char> destination,
        out int charsWritten
    ) {
        var integerPart = (magnitude >> fractionBitCount);
        var fraction = magnitude & ((1UL << fractionBitCount) - 1UL);
        // The exact required length is a pure function of the raw, so the length check runs before any write and the
        // caller's destination is genuinely all-or-nothing. Each rendered fraction digit multiplies by ten, which
        // strips exactly one factor of two from the denominator, so the expansion terminates after fractionBitCount
        // minus the fraction's trailing zero count digits.
        var requiredLength = ((negative
            ? 1
            : 0) + ((int)integerPart.LogarithmBase10()));

        if (fraction != 0UL) {
            requiredLength += (1 + (fractionBitCount - BitOperations.TrailingZeroCount(value: fraction)));
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
            position += WriteFractionDigits(
                fraction: fraction,
                fractionBitCount: fractionBitCount,
                destination: destination[position..]
            );
        }

        charsWritten = position;

        return true;
    }
    /// <summary>Writes a signed raw value using a provider's decimal-separator and negative-sign tokens.</summary>
    /// <param name="rawValue">The signed raw value.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <param name="destination">The span the expansion is written to.</param>
    /// <param name="charsWritten">The characters written, or zero when the destination is too small.</param>
    /// <param name="provider">The provider whose number tokens are spliced into the invariant expansion.</param>
    /// <returns>Whether the complete expansion fit. A refusal leaves <paramref name="destination"/> untouched.</returns>
    internal static bool TryFormatSigned(
        long rawValue,
        int fractionBitCount,
        Span<char> destination,
        out int charsWritten,
        IFormatProvider? provider
    ) =>
        TryFormat(
            magnitude: FusedArithmetic.RawMagnitude(value: rawValue),
            negative: (rawValue < 0L),
            fractionBitCount: fractionBitCount,
            destination: destination,
            charsWritten: out charsWritten,
            provider: provider
        );
    /// <summary>Refuses any format specifier other than the exact decimal expansion, then writes a signed raw value
    /// using a provider's number tokens.</summary>
    /// <param name="rawValue">The signed raw value.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <param name="format">An empty format, <c>G</c> or <c>g</c>.</param>
    /// <param name="destination">The span the expansion is written to.</param>
    /// <param name="charsWritten">The characters written, or zero when the destination is too small.</param>
    /// <param name="provider">The provider whose number tokens are spliced into the invariant expansion.</param>
    /// <returns>Whether the complete expansion fit. A refusal leaves <paramref name="destination"/> untouched.</returns>
    /// <exception cref="FormatException"><paramref name="format"/> is another specifier.</exception>
    internal static bool TryFormatSignedGeneral(
        long rawValue,
        int fractionBitCount,
        ReadOnlySpan<char> format,
        Span<char> destination,
        out int charsWritten,
        IFormatProvider? provider
    ) {
        ValidateGeneralFormat(format: format);

        return TryFormatSigned(
            charsWritten: out charsWritten,
            destination: destination,
            fractionBitCount: fractionBitCount,
            provider: provider,
            rawValue: rawValue
        );
    }
    /// <summary>Parses text into the raw of a signed 64-bit fixed-point carrier, reporting instead of
    /// throwing.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="style">The styles <paramref name="s"/> is admitted under.</param>
    /// <param name="provider">The provider supplying the numeric conventions, or <see langword="null"/> for the
    /// invariant culture.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <param name="parsingDenominator">The carrier's <see cref="CreateParsingDenominator"/> value.</param>
    /// <param name="rawValue">The parsed raw value, or zero on failure.</param>
    /// <returns>Whether <paramref name="s"/> named an in-range value.</returns>
    internal static bool TryParseSignedRaw(
        ReadOnlySpan<char> s,
        NumberStyles style,
        IFormatProvider? provider,
        int fractionBitCount,
        UInt128 parsingDenominator,
        out long rawValue
    ) =>
        (FixedPointParseStatus.Success == ParseSignedRawCore(
            fractionBitCount: fractionBitCount,
            parsingDenominator: parsingDenominator,
            provider: provider,
            rawValue: out rawValue,
            s: s,
            style: style
        ));
    /// <summary>Refuses any format specifier other than the exact decimal expansion.</summary>
    /// <param name="format">The specifier to validate.</param>
    /// <exception cref="FormatException">The specifier is neither empty nor <c>G</c>/<c>g</c>.</exception>
    internal static void ValidateGeneralFormat(ReadOnlySpan<char> format) {
        if (
            !format.IsEmpty &&
            !format.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "G"
        )
        ) {
            throw new FormatException(message: $"The '{format.ToString()}' format is not supported. Use 'G' for the exact decimal expansion.");
        }
    }
    /// <summary>Writes the decimal point and the exact terminating expansion of a raw fraction.</summary>
    /// <param name="fraction">The raw fraction bits, strictly below <c>2^fractionBitCount</c>.</param>
    /// <param name="fractionBitCount">The carrier's fraction bit count.</param>
    /// <param name="destination">The span the expansion is written to, starting at the decimal point.</param>
    /// <returns>The characters written, or <c>-1</c> when <paramref name="destination"/> was too small — in which case
    /// the characters that did fit have already been written.</returns>
    /// <remarks>Each digit multiplies by ten, stripping exactly one factor of two from the
    /// <paramref name="fractionBitCount"/> the denominator holds, so the expansion terminates after
    /// <paramref name="fractionBitCount"/> minus the fraction's trailing zero count digits.</remarks>
    internal static int WriteFractionDigits(ulong fraction, int fractionBitCount, Span<char> destination) {
        if (destination.IsEmpty) {
            return -1;
        }

        var mask = ((1UL << fractionBitCount) - 1UL);
        var position = 0;

        destination[position++] = '.';

        do {
            if (destination.Length <= position) {
                return -1;
            }

            fraction *= 10UL;
            destination[position++] = ((char)('0' + ((int)(fraction >> fractionBitCount))));
            fraction &= mask;
        } while (0UL != fraction);

        return position;
    }

    private static bool ContainsAmbiguousFreeToken(ReadOnlySpan<char> s, ReadOnlySpan<char> token) {
        if (
            token.IsEmpty ||
            !s.Contains(
            comparisonType: StringComparison.Ordinal,
            value: token
        )
        ) {
            return false;
        }

        foreach (var value in token) {
            if (((uint)(value - '0')) <= 9U) {
                return true;
            }
        }

        return false;
    }
    private static bool ContainsExponentMarkerToken(ReadOnlySpan<char> s, string token) =>
        (!string.IsNullOrEmpty(value: token) &&
        (token.Contains(value: 'e') || token.Contains(value: 'E')) &&
        s.Contains(
            comparisonType: StringComparison.Ordinal,
            value: token
        ));
    private static bool ContainsToken(ReadOnlySpan<char> s, string token) =>
        (!string.IsNullOrEmpty(value: token) &&
        s.Contains(
            comparisonType: StringComparison.Ordinal,
            value: token
        ));
    /// <summary>
    /// Locates the exponent marker and reads its magnitude, saturating at <paramref name="exponentLimit"/>.
    /// </summary>
    /// <remarks>
    /// The limit is <c>s.Length + fractionBitCount + 21</c>, and saturating there is value-preserving rather than
    /// merely convenient. The significand carries at most <c>s.Length</c> digits, so with <c>d</c> the digit index of
    /// the decimal point and <c>z</c> the leading-zero count the scanner's integer digit count is
    /// <c>d + exponent − z</c>, with <c>0 ≤ d ≤ s.Length</c> and <c>0 ≤ z ≤ s.Length</c>. An exponent at or above the
    /// limit therefore leaves at least twenty-one integer digits, which the twenty-digit gate rejects as an overflow
    /// whatever the true exponent was; an exponent at or below its negative leaves the digit count at or below
    /// <c>−(fractionBitCount + 1)</c>, which pushes every stored digit past the fraction prefix and quantizes to zero
    /// whatever the true exponent was. Both saturated verdicts are the verdicts the unsaturated exponent produces, so
    /// no accepted syntax changes value — unlike a fixed cap, which a long enough run of leading fractional zeros can
    /// compensate back into range.
    /// </remarks>
    private static int FindExponent(
        ReadOnlySpan<char> s,
        NumberStyles style,
        NumberFormatInfo numberFormat,
        ReadOnlySpan<char> decimalSeparator,
        ReadOnlySpan<char> groupSeparator,
        long exponentLimit,
        out long exponent
    ) {
        exponent = 0L;

        if (0 == (style & NumberStyles.AllowExponent)) {
            return -1;
        }

        var hasPriorDigit = false;

        for (var index = 0; (index < s.Length); index++) {
            if (
                (0 != (style & NumberStyles.AllowDecimalPoint)) &&
                !decimalSeparator.IsEmpty &&
                s[index..].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: decimalSeparator
            )
            ) {
                index += (decimalSeparator.Length - 1);

                continue;
            }

            if (
                (0 != (style & NumberStyles.AllowThousands)) &&
                !groupSeparator.IsEmpty &&
                s[index..].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: groupSeparator
            )
            ) {
                index += (groupSeparator.Length - 1);

                continue;
            }

            var digit = (s[index] - '0');

            if (((uint)digit) <= 9U) {
                hasPriorDigit = true;

                continue;
            }

            if (
                !hasPriorDigit ||
                (s[index] is not ('e' or 'E'))
            ) {
                continue;
            }

            var exponentIndex = (index + 1);
            var negativeExponent = false;

            if (MatchesToken(
                s: s,
                index: exponentIndex,
                token: numberFormat.PositiveSign
            )) {
                exponentIndex += numberFormat.PositiveSign.Length;
            } else if (MatchesToken(
                s: s,
                index: exponentIndex,
                token: numberFormat.NegativeSign
            )) {
                negativeExponent = true;
                exponentIndex += numberFormat.NegativeSign.Length;
            }

            var exponentStart = exponentIndex;
            var magnitude = 0L;

            while (exponentIndex < s.Length) {
                digit = (s[exponentIndex] - '0');

                if (((uint)digit) > 9U) {
                    break;
                }

                // magnitude is already at or below exponentLimit, which is bounded by int.MaxValue + 53, so the
                // accumulation cannot leave long however long the exponent's digit run is.
                magnitude = Math.Min(
                    val1: ((magnitude * 10L) + digit),
                    val2: exponentLimit
                );
                exponentIndex++;
            }

            if (exponentStart == exponentIndex) {
                continue;
            }

            exponent = (negativeExponent
                ? -magnitude
                : magnitude
            );

            return index;
        }

        return -1;
    }
    // Two tokens alias when the shorter is a prefix of the longer AND the input carries the shorter one: from that
    // point on, a containment test cannot say which token the text spells. Equality is the degenerate case.
    private static bool HasAliasedTokens(ReadOnlySpan<char> s, string first, string second) {
        if (
            string.IsNullOrEmpty(value: first) ||
            string.IsNullOrEmpty(value: second)
        ) {
            return false;
        }

        var shorter = ((first.Length <= second.Length)
            ? first
            : second
        );
        var longer = ((first.Length <= second.Length)
            ? second
            : first
        );

        return (
            longer.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: shorter
        ) &&
            s.Contains(
            comparisonType: StringComparison.Ordinal,
            value: shorter
        )
        );
    }
    private static bool HasAmbiguousEnabledFormatToken(
        ReadOnlySpan<char> s,
        NumberStyles style,
        NumberFormatInfo numberFormat,
        bool useCurrencySeparators
    ) {
        // The invariant format is immutable and its tokens ('+', '-', '.', ',', '¤') carry no digit, prefix one
        // another, or begin with parser white space, so none of the shapes below can arise from it.
        if (ReferenceEquals(objA: numberFormat, objB: NumberFormatInfo.InvariantInfo)) { return false; }

        var signEnabled = (0 != (style & (NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign | NumberStyles.AllowExponent)));

        if (
            signEnabled &&
            (ContainsAmbiguousFreeToken(
            s: s,
            token: numberFormat.PositiveSign
        ) ||
             ContainsAmbiguousFreeToken(
            s: s,
            token: numberFormat.NegativeSign
        ))
        ) {
            return true;
        }

        // The separator family this scanner selected — the tokens the main scan and exponent discovery will match.
        var decimalSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyDecimalSeparator
            : numberFormat.NumberDecimalSeparator
        );
        var groupSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyGroupSeparator
            : numberFormat.NumberGroupSeparator
        );

        // A separator that aliases an enabled sign token splits the classifications by POSITION: the platform reads
        // the token as a sign where the grammar admits one, while this scanner matches separators wherever they
        // stand, so the same text can name two numbers.
        if (
            signEnabled &&
            (HasAliasedTokens(
            s: s,
            first: decimalSeparator,
            second: numberFormat.PositiveSign
        ) ||
             HasAliasedTokens(
            s: s,
            first: decimalSeparator,
            second: numberFormat.NegativeSign
        ) ||
             HasAliasedTokens(
            s: s,
            first: groupSeparator,
            second: numberFormat.PositiveSign
        ) ||
             HasAliasedTokens(
            s: s,
            first: groupSeparator,
            second: numberFormat.NegativeSign
        ))
        ) {
            return true;
        }

        // The platform consumes leading and trailing white space in its own phase before any token is matched, so a
        // decimal separator whose text BEGINS with parser white space can be consumed there while this scanner reads
        // it as the radix point. A group separator needs no twin check: skipping it is value-preserving on both
        // sides.
        if (
            (0 != (style & (NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite))) &&
            (0 != decimalSeparator.Length) &&
            IsParserWhitespace(value: decimalSeparator[0]) &&
            ContainsToken(
            s: s,
            token: decimalSeparator
        )
        ) {
            return true;
        }

        // Exponent discovery skips separator matches before testing for 'e'/'E', while the platform's state machine
        // classifies a marker by grammar position — one consumed decimal separator, no group separator past it — so
        // an enabled separator token CARRYING the marker can make the two passes split the digits differently.
        if (
            (0 != (style & NumberStyles.AllowExponent)) &&
            (((0 != (style & NumberStyles.AllowDecimalPoint)) && ContainsExponentMarkerToken(
            s: s,
            token: decimalSeparator
        )) ||
             ((0 != (style & NumberStyles.AllowThousands)) && ContainsExponentMarkerToken(
            s: s,
            token: groupSeparator
        )))
        ) {
            return true;
        }

        if (0 == (style & NumberStyles.AllowCurrencySymbol)) {
            return false;
        }

        if (
            useCurrencySeparators &&
            ContainsAmbiguousFreeToken(
            s: s,
            token: numberFormat.CurrencySymbol
        )
        ) {
            return true;
        }

        // The platform consumes the whole currency symbol as one token, so a symbol that CONTAINS the active decimal
        // separator hides that separator from the validator while this scanner still finds it inside the symbol's
        // own characters — no family choice can mend a split inside a single token. A group separator inside the
        // symbol needs no twin check: skipping it is value-preserving on both sides.
        if (
            ContainsToken(
            s: s,
            token: numberFormat.CurrencySymbol
        ) &&
            TokenContainsToken(
            outer: numberFormat.CurrencySymbol,
            inner: decimalSeparator
        )
        ) {
            return true;
        }

        // The currency symbol is what UsesCurrencySeparators selects the separator family on, and the platform parser
        // classifies the same token by grammar and position instead. When the symbol aliases an enabled sign token the
        // two classifications part company — the validator reads a plain signed number, this scanner reads currency
        // syntax — so the family choice is unfounded and the input is refused.
        if (
            signEnabled &&
            (HasAliasedTokens(
            s: s,
            first: numberFormat.CurrencySymbol,
            second: numberFormat.NegativeSign
        ) ||
             HasAliasedTokens(
            s: s,
            first: numberFormat.CurrencySymbol,
            second: numberFormat.PositiveSign
        ))
        ) {
            return true;
        }

        // With a currency symbol admitted the platform classifies a separator by POSITION: the currency family once a
        // currency symbol has been consumed, either family before one. UsesCurrencySeparators picks a family for the
        // whole input and cannot see that position, so wherever the two families disagree the exact scanner can split
        // the digits where the validator did not. When they agree — every built-in culture, and every provider that
        // sets both faces together — there is nothing to disagree about and the input is scanned as usual.
        if (
            string.Equals(
            a: numberFormat.NumberDecimalSeparator,
            b: numberFormat.CurrencyDecimalSeparator,
            comparisonType: StringComparison.Ordinal
        ) &&
            string.Equals(
            a: numberFormat.NumberGroupSeparator,
            b: numberFormat.CurrencyGroupSeparator,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return false;
        }

        return (
            ContainsToken(
            s: s,
            token: numberFormat.NumberDecimalSeparator
        ) ||
            ContainsToken(
            s: s,
            token: numberFormat.CurrencyDecimalSeparator
        ) ||
            ContainsToken(
            s: s,
            token: numberFormat.NumberGroupSeparator
        ) ||
            ContainsToken(
            s: s,
            token: numberFormat.CurrencyGroupSeparator
        )
        );
    }
    // The fixed set the platform's number parser skips as white space: 0x20 and 0x09 through 0x0D.
    private static bool IsParserWhitespace(char value) =>
        ((' ' == value) || (('\t' <= value) && ('\r' >= value)));
    private static bool MatchesToken(ReadOnlySpan<char> s, int index, string token) =>
        (!string.IsNullOrEmpty(value: token) &&
        (((uint)index) <= ((uint)s.Length)) &&
        s[index..].StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: token
        ));
    // The signed carriers differ only in fraction bit count and parsing denominator: all three reserve one raw beyond
    // long.MaxValue on the negative side, and a magnitude of exactly 2^63 is long.MinValue, which no negation of a
    // long can name, so it is spelled directly rather than negated.
    private static FixedPointParseStatus ParseSignedRawCore(
        ReadOnlySpan<char> s,
        NumberStyles style,
        IFormatProvider? provider,
        int fractionBitCount,
        UInt128 parsingDenominator,
        out long rawValue
    ) {
        rawValue = 0L;

        var status = Parse(
            fractionBitCount: fractionBitCount,
            maximumNegativeMagnitudeRaw: (1UL << 63),
            maximumPositiveRaw: long.MaxValue,
            negative: out var negative,
            parsingDenominator: parsingDenominator,
            provider: provider,
            rawMagnitude: out var rawMagnitude,
            rejectExactOutOfRange: false,
            s: s,
            style: style
        );

        if (FixedPointParseStatus.Success == status) {
            rawValue = (negative
                ? ((rawMagnitude == (1UL << 63))
                    ? long.MinValue
                    : -((long)rawMagnitude))
                : ((long)rawMagnitude)
            );
        }

        return status;
    }
    private static bool TokenContainsToken(string outer, string inner) =>
        (!string.IsNullOrEmpty(value: outer) &&
        !string.IsNullOrEmpty(value: inner) &&
        outer.Contains(
            comparisonType: StringComparison.Ordinal,
            value: inner
        ));
    private static bool TrySpliceProviderTokens(
        ReadOnlySpan<char> invariant,
        NumberFormatInfo numberFormat,
        Span<char> destination,
        out int charsWritten
    ) {
        var body = invariant;
        var negative = (body[0] == '-');

        if (negative) {
            body = body[1..];
        }

        var separator = numberFormat.NumberDecimalSeparator;
        var negativeSign = numberFormat.NegativeSign;
        var pointIndex = body.IndexOf(value: '.');
        var signLength = (negative
            ? negativeSign.Length
            : 0
        );
        var requiredLength = ((signLength + body.Length) + ((pointIndex < 0)
            ? 0
            : (separator.Length - 1)));

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
    private static bool UsesCurrencySeparators(
        ReadOnlySpan<char> s,
        NumberStyles style,
        NumberFormatInfo numberFormat
    ) {
        if (0 == (style & NumberStyles.AllowCurrencySymbol)) {
            return false;
        }

        if (
            !string.IsNullOrEmpty(value: numberFormat.CurrencySymbol) &&
            s.Contains(
            value: numberFormat.CurrencySymbol,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return true;
        }

        return
            (
            !string.IsNullOrEmpty(value: numberFormat.CurrencyDecimalSeparator) &&
            !s.Contains(
            value: numberFormat.NumberDecimalSeparator,
            comparisonType: StringComparison.Ordinal
        ) &&
            s.Contains(
            value: numberFormat.CurrencyDecimalSeparator,
            comparisonType: StringComparison.Ordinal
        )
        );
    }
}
