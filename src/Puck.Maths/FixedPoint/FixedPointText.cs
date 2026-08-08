using System.Globalization;

namespace Puck.Maths;

internal enum FixedPointParseStatus {
    Success,
    Invalid,
    Overflow
}

/// <summary>Exact, allocation-free decimal parsing and format validation shared by the fixed-point primitives.</summary>
internal static class FixedPointText {
    private const int StoredSignificantDigitCount = 64;

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

    /// <summary>Refuses any format specifier other than the exact decimal expansion.</summary>
    /// <param name="format">The specifier to validate.</param>
    /// <exception cref="FormatException">The specifier is neither empty nor <c>G</c>/<c>g</c>.</exception>
    internal static void ValidateGeneralFormat(ReadOnlySpan<char> format) {
        if (!format.IsEmpty && !format.Equals(other: "G", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            throw new FormatException(message: $"The '{format.ToString()}' format is not supported. Use 'G' for the exact decimal expansion.");
        }
    }

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
            exponentLimit: ((((long)s.Length) + fractionBitCount) + 21L),
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

        for (var index = 0; (index < significand.Length);) {
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

        for (var digitIndex = 0L; (digitIndex < integerSignificantDigitCount); digitIndex++) {
            var digit = ((digitIndex < storedDigitCount)
                ? significantDigits[((int)digitIndex)]
                : ((byte)0));

            integerPart = ((integerPart * 10U) + digit);
        }

        var integerRaw = (integerPart << fractionBitCount);
        var fractionDigitLimit = (fractionBitCount + 1);
        var fractionPrefix = UInt128.Zero;

        for (var fractionIndex = 0; (fractionIndex < fractionDigitLimit); fractionIndex++) {
            var significantIndex = (integerSignificantDigitCount + fractionIndex);
            var digit = (((0L <= significantIndex) && (significantIndex < storedDigitCount))
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
            (!string.IsNullOrEmpty(value: numberFormat.CurrencyDecimalSeparator) &&
            !s.Contains(
                value: numberFormat.NumberDecimalSeparator,
                comparisonType: StringComparison.Ordinal
            ) &&
            s.Contains(
                value: numberFormat.CurrencyDecimalSeparator,
                comparisonType: StringComparison.Ordinal
            ));
    }
    private static bool MatchesToken(ReadOnlySpan<char> s, int index, string token) =>
        (!string.IsNullOrEmpty(value: token) &&
        ((uint)index <= ((uint)s.Length)) &&
        s[index..].StartsWith(
            value: token,
            comparisonType: StringComparison.Ordinal
        ));
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

    // Two tokens alias when the shorter is a prefix of the longer AND the input carries the shorter one: from that
    // point on, a containment test cannot say which token the text spells. Equality is the degenerate case.
    private static bool HasAliasedTokens(ReadOnlySpan<char> s, string first, string second) {
        if (string.IsNullOrEmpty(value: first) || string.IsNullOrEmpty(value: second)) {
            return false;
        }

        var shorter = ((first.Length <= second.Length) ? first : second);
        var longer = ((first.Length <= second.Length) ? second : first);

        return (longer.StartsWith(value: shorter, comparisonType: StringComparison.Ordinal) &&
               s.Contains(value: shorter, comparisonType: StringComparison.Ordinal));
    }
    private static bool HasAmbiguousEnabledFormatToken(
        ReadOnlySpan<char> s,
        NumberStyles style,
        NumberFormatInfo numberFormat,
        bool useCurrencySeparators
    ) {
        var signEnabled = (0 != (style & (NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign | NumberStyles.AllowExponent)));

        if (signEnabled &&
            (ContainsAmbiguousFreeToken(s: s, token: numberFormat.PositiveSign) ||
             ContainsAmbiguousFreeToken(s: s, token: numberFormat.NegativeSign))) {
            return true;
        }

        // The separator family this scanner selected — the tokens the main scan and exponent discovery will match.
        var decimalSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyDecimalSeparator
            : numberFormat.NumberDecimalSeparator);
        var groupSeparator = (useCurrencySeparators
            ? numberFormat.CurrencyGroupSeparator
            : numberFormat.NumberGroupSeparator);

        // A separator that aliases an enabled sign token splits the classifications by POSITION: the platform reads
        // the token as a sign where the grammar admits one, while this scanner matches separators wherever they
        // stand, so the same text can name two numbers.
        if (signEnabled &&
            (HasAliasedTokens(s: s, first: decimalSeparator, second: numberFormat.PositiveSign) ||
             HasAliasedTokens(s: s, first: decimalSeparator, second: numberFormat.NegativeSign) ||
             HasAliasedTokens(s: s, first: groupSeparator, second: numberFormat.PositiveSign) ||
             HasAliasedTokens(s: s, first: groupSeparator, second: numberFormat.NegativeSign))) {
            return true;
        }

        // The platform consumes leading and trailing white space in its own phase before any token is matched, so a
        // decimal separator whose text BEGINS with parser white space can be consumed there while this scanner reads
        // it as the radix point. A group separator needs no twin check: skipping it is value-preserving on both
        // sides.
        if ((0 != (style & (NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite))) &&
            (0 != decimalSeparator.Length) &&
            IsParserWhitespace(value: decimalSeparator[0]) &&
            ContainsToken(s: s, token: decimalSeparator)) {
            return true;
        }

        // Exponent discovery skips separator matches before testing for 'e'/'E', while the platform's state machine
        // classifies a marker by grammar position — one consumed decimal separator, no group separator past it — so
        // an enabled separator token CARRYING the marker can make the two passes split the digits differently.
        if ((0 != (style & NumberStyles.AllowExponent)) &&
            (((0 != (style & NumberStyles.AllowDecimalPoint)) && ContainsExponentMarkerToken(s: s, token: decimalSeparator)) ||
             ((0 != (style & NumberStyles.AllowThousands)) && ContainsExponentMarkerToken(s: s, token: groupSeparator)))) {
            return true;
        }

        if (0 == (style & NumberStyles.AllowCurrencySymbol)) {
            return false;
        }

        if (useCurrencySeparators &&
            ContainsAmbiguousFreeToken(s: s, token: numberFormat.CurrencySymbol)) {
            return true;
        }

        // The platform consumes the whole currency symbol as one token, so a symbol that CONTAINS the active decimal
        // separator hides that separator from the validator while this scanner still finds it inside the symbol's
        // own characters — no family choice can mend a split inside a single token. A group separator inside the
        // symbol needs no twin check: skipping it is value-preserving on both sides.
        if (ContainsToken(s: s, token: numberFormat.CurrencySymbol) &&
            TokenContainsToken(outer: numberFormat.CurrencySymbol, inner: decimalSeparator)) {
            return true;
        }

        // The currency symbol is what UsesCurrencySeparators selects the separator family on, and the platform parser
        // classifies the same token by grammar and position instead. When the symbol aliases an enabled sign token the
        // two classifications part company — the validator reads a plain signed number, this scanner reads currency
        // syntax — so the family choice is unfounded and the input is refused.
        if (signEnabled &&
            (HasAliasedTokens(s: s, first: numberFormat.CurrencySymbol, second: numberFormat.NegativeSign) ||
             HasAliasedTokens(s: s, first: numberFormat.CurrencySymbol, second: numberFormat.PositiveSign))) {
            return true;
        }

        // With a currency symbol admitted the platform classifies a separator by POSITION: the currency family once a
        // currency symbol has been consumed, either family before one. UsesCurrencySeparators picks a family for the
        // whole input and cannot see that position, so wherever the two families disagree the exact scanner can split
        // the digits where the validator did not. When they agree — every built-in culture, and every provider that
        // sets both faces together — there is nothing to disagree about and the input is scanned as usual.
        if (string.Equals(a: numberFormat.NumberDecimalSeparator, b: numberFormat.CurrencyDecimalSeparator, comparisonType: StringComparison.Ordinal) &&
            string.Equals(a: numberFormat.NumberGroupSeparator, b: numberFormat.CurrencyGroupSeparator, comparisonType: StringComparison.Ordinal)) {
            return false;
        }

        return (ContainsToken(s: s, token: numberFormat.NumberDecimalSeparator) ||
               ContainsToken(s: s, token: numberFormat.CurrencyDecimalSeparator) ||
               ContainsToken(s: s, token: numberFormat.NumberGroupSeparator) ||
               ContainsToken(s: s, token: numberFormat.CurrencyGroupSeparator));
    }
    private static bool ContainsToken(ReadOnlySpan<char> s, string token) =>
        (!string.IsNullOrEmpty(value: token) &&
        s.Contains(value: token, comparisonType: StringComparison.Ordinal));

    // The fixed set the platform's number parser skips as white space: 0x20 and 0x09 through 0x0D.
    private static bool IsParserWhitespace(char value) =>
        ((' ' == value) || (('\t' <= value) && ('\r' >= value)));
    private static bool ContainsExponentMarkerToken(ReadOnlySpan<char> s, string token) =>
        (!string.IsNullOrEmpty(value: token) &&
        (token.Contains(value: 'e') || token.Contains(value: 'E')) &&
        s.Contains(value: token, comparisonType: StringComparison.Ordinal));
    private static bool TokenContainsToken(string outer, string inner) =>
        (!string.IsNullOrEmpty(value: outer) &&
        !string.IsNullOrEmpty(value: inner) &&
        outer.Contains(value: inner, comparisonType: StringComparison.Ordinal));
}
