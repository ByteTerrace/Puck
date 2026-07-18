using System.Globalization;

namespace Puck.Maths;

internal enum FixedPointParseStatus {
    Success,
    Invalid,
    Overflow
}

/// <summary>Exact, allocation-free decimal parsing shared by the fixed-point primitives.</summary>
internal static class FixedPointText {
    private const int StoredSignificantDigitCount = 64;

    /// <summary>
    /// Creates <c>2 × 5^(fractionBitCount + 1)</c>, the reduced denominator obtained when a decimal prefix with
    /// <c>fractionBitCount + 1</c> digits is scaled by <c>2^fractionBitCount</c>.
    /// </summary>
    internal static UInt128 CreateParsingDenominator(int fractionBitCount) {
        var powerOfFive = UInt128.One;

        for (var i = 0; i <= fractionBitCount; i++) {
            powerOfFive *= 5U;
        }

        return (powerOfFive << 1);
    }

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
            s: s,
            style: style,
            provider: effectiveProvider,
            result: out var validated
        )) {
            return FixedPointParseStatus.Invalid;
        }

        negative = (validated < 0m);

        var numberFormat = NumberFormatInfo.GetInstance(formatProvider: effectiveProvider);
        var useCurrencySeparators = UsesCurrencySeparators(
            s: s,
            style: style,
            numberFormat: numberFormat
        );
        ReadOnlySpan<char> decimalSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyDecimalSeparator
            : numberFormat.NumberDecimalSeparator);
        ReadOnlySpan<char> groupSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyGroupSeparator
            : numberFormat.NumberGroupSeparator);

        // The exact scanner must be able to distinguish syntax tokens from significand digits and the exponent
        // marker. Built-in cultures (including alphabetic currency symbols and multi-character tokens) satisfy that
        // requirement. Separator tokens are handled explicitly during exponent discovery. A hand-built NFI can still
        // make a free-form sign/currency token contain digits; accepting that shape could let the BCL validate one
        // number while the exact pass quantizes another. Reject that ambiguity instead of returning wrong raw bits.
        // A hand-built NFI can also alias its currency symbol with a sign token; UsesCurrencySeparators then reads
        // a plain signed number as currency-formatted and scans with the currency separators — no built-in culture
        // has that shape, and it is accepted as-is rather than detected.
        if (HasAmbiguousEnabledFormatToken(
            s: s,
            style: style,
            numberFormat: numberFormat,
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
            exponent: out var exponent
        );
        var significand = ((0 <= exponentIndex) ? s[..exponentIndex] : s);
        Span<byte> significantDigits = stackalloc byte[StoredSignificantDigitCount];
        var storedDigitCount = 0;
        var totalDigitCount = 0L;
        var leadingZeroCount = 0L;
        var decimalDigitIndex = -1L;
        var lastNonzeroSignificantIndex = -1L;
        var seenNonzero = false;

        for (var index = 0; index < significand.Length;) {
            var digit = (significand[index] - '0');

            if ((uint)digit <= 9U) {
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
                    value: decimalSeparator,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                decimalDigitIndex = totalDigitCount;
                index += decimalSeparator.Length;

                continue;
            }

            if (
                !groupSeparator.IsEmpty &&
                significand[index..].StartsWith(
                    value: groupSeparator,
                    comparisonType: StringComparison.Ordinal
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

        for (var digitIndex = 0L; digitIndex < integerSignificantDigitCount; digitIndex++) {
            var digit = ((digitIndex < storedDigitCount)
                ? significantDigits[((int)digitIndex)]
                : ((byte)0));

            integerPart = ((integerPart * 10U) + digit);
        }

        var integerRaw = (integerPart << fractionBitCount);
        var fractionDigitLimit = (fractionBitCount + 1);
        var fractionPrefix = UInt128.Zero;

        for (var fractionIndex = 0; fractionIndex < fractionDigitLimit; fractionIndex++) {
            var significantIndex = (integerSignificantDigitCount + fractionIndex);
            var digit = ((0L <= significantIndex) && (significantIndex < storedDigitCount)
                ? significantDigits[((int)significantIndex)]
                : ((byte)0));

            fractionPrefix = ((fractionPrefix * 10U) + digit);
        }

        var hasNonzeroDiscardedFractionDigit =
            (lastNonzeroSignificantIndex >= (integerSignificantDigitCount + fractionDigitLimit));
        var maximumRaw = (negative ? maximumNegativeMagnitudeRaw : maximumPositiveRaw);

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

        var fractionRaw = (fractionPrefix / parsingDenominator);
        var remainder = (fractionPrefix - (fractionRaw * parsingDenominator));
        var half = (parsingDenominator >> 1);

        if (
            (remainder > half) ||
            (
                (remainder == half) &&
                (
                    hasNonzeroDiscardedFractionDigit ||
                    !UInt128.IsEvenInteger(value: fractionRaw)
                )
            )
        ) {
            fractionRaw++;
        }

        var roundedRaw = (integerRaw + fractionRaw);

        if (roundedRaw > maximumRaw) {
            return FixedPointParseStatus.Overflow;
        }

        rawMagnitude = ((ulong)roundedRaw);
        negative &= (0UL != rawMagnitude);

        return FixedPointParseStatus.Success;
    }

    private static int FindExponent(
        ReadOnlySpan<char> s,
        NumberStyles style,
        NumberFormatInfo numberFormat,
        ReadOnlySpan<char> decimalSeparator,
        ReadOnlySpan<char> groupSeparator,
        out long exponent
    ) {
        exponent = 0L;

        if (0 == (style & NumberStyles.AllowExponent)) {
            return -1;
        }

        var hasPriorDigit = false;

        for (var index = 0; index < s.Length; index++) {
            if ((0 != (style & NumberStyles.AllowDecimalPoint)) &&
                !decimalSeparator.IsEmpty &&
                s[index..].StartsWith(value: decimalSeparator, comparisonType: StringComparison.Ordinal)) {
                index += (decimalSeparator.Length - 1);

                continue;
            }

            if ((0 != (style & NumberStyles.AllowThousands)) &&
                !groupSeparator.IsEmpty &&
                s[index..].StartsWith(value: groupSeparator, comparisonType: StringComparison.Ordinal)) {
                index += (groupSeparator.Length - 1);

                continue;
            }

            var digit = (s[index] - '0');

            if ((uint)digit <= 9U) {
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

            if (MatchesToken(s: s, index: exponentIndex, token: numberFormat.PositiveSign)) {
                exponentIndex += numberFormat.PositiveSign.Length;
            } else if (MatchesToken(s: s, index: exponentIndex, token: numberFormat.NegativeSign)) {
                negativeExponent = true;
                exponentIndex += numberFormat.NegativeSign.Length;
            }

            var exponentStart = exponentIndex;
            var magnitude = 0L;

            while (exponentIndex < s.Length) {
                digit = (s[exponentIndex] - '0');

                if ((uint)digit > 9U) {
                    break;
                }

                magnitude = Math.Min(
                    val1: ((magnitude * 10L) + digit),
                    val2: 1_000_000L
                );
                exponentIndex++;
            }

            if (exponentStart == exponentIndex) {
                continue;
            }

            exponent = (negativeExponent ? -magnitude : magnitude);

            return index;
        }

        return -1;
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
            !string.IsNullOrEmpty(value: numberFormat.CurrencyDecimalSeparator) &&
            !s.Contains(
                value: numberFormat.NumberDecimalSeparator,
                comparisonType: StringComparison.Ordinal
            ) &&
            s.Contains(
                value: numberFormat.CurrencyDecimalSeparator,
                comparisonType: StringComparison.Ordinal
            );
    }

    private static bool MatchesToken(ReadOnlySpan<char> s, int index, string token) =>
        !string.IsNullOrEmpty(value: token) &&
        ((uint)index <= ((uint)s.Length)) &&
        s[index..].StartsWith(
            value: token,
            comparisonType: StringComparison.Ordinal
        );

    private static bool ContainsAmbiguousFreeToken(ReadOnlySpan<char> s, ReadOnlySpan<char> token) {
        if (token.IsEmpty ||
            !s.Contains(value: token, comparisonType: StringComparison.Ordinal)) {
            return false;
        }

        foreach (var value in token) {
            if ((uint)(value - '0') <= 9U) {
                return true;
            }
        }

        return false;
    }

    private static bool HasAmbiguousEnabledFormatToken(
        ReadOnlySpan<char> s,
        NumberStyles style,
        NumberFormatInfo numberFormat,
        bool useCurrencySeparators
    ) {
        if ((0 != (style & (NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign | NumberStyles.AllowExponent))) &&
            (ContainsAmbiguousFreeToken(s: s, token: numberFormat.PositiveSign) ||
             ContainsAmbiguousFreeToken(s: s, token: numberFormat.NegativeSign))) {
            return true;
        }

        return useCurrencySeparators &&
               (0 != (style & NumberStyles.AllowCurrencySymbol)) &&
               ContainsAmbiguousFreeToken(s: s, token: numberFormat.CurrencySymbol);
    }
}
