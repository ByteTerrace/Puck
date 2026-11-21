namespace Puck.Abstractions;

/// <summary>
/// The one reading of "the integer a name spells": exactly the text <see cref="long.ToString(IFormatProvider)"/> gives
/// under the invariant culture, so a name parses to <c>n</c> only when <c>n</c> prints as that name. Digits only, an
/// optional leading <c>-</c>, no leading zero except <c>0</c> itself, no <c>-0</c>, no <c>+</c>, no white space, and
/// no culture. <c>007</c> and <c>+7</c> spell no integer, because <c>7</c> does not print that way.
/// </summary>
/// <remarks><see cref="int.TryParse(string, out int)"/> and its kin accept several spellings of one value, so two
/// readers built on them can disagree with a reader that prints the value and compares names.</remarks>
public static class CanonicalDecimal {
    /// <summary>Attempts to read the integer a name spells in canonical decimal.</summary>
    /// <param name="text">The name.</param>
    /// <param name="value">The integer on success; otherwise zero.</param>
    /// <returns><see langword="true"/> when the name is the canonical decimal spelling of a <see cref="long"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out long value) {
        value = 0L;

        var negative = ((text.Length > 1) && (text[0] == '-'));
        var digits = (negative ? text[1..] : text);

        if (digits.IsEmpty || (digits.Length > 19) || ((digits[0] == '0') && ((digits.Length > 1) || negative))) {
            return false;
        }

        // Accumulates the magnitude negated, so long.MinValue, whose magnitude long cannot hold, reads exactly.
        var accumulated = 0L;

        foreach (var digit in digits) {
            if (!char.IsAsciiDigit(c: digit) || (accumulated < ((long.MinValue + (digit - '0')) / 10L))) {
                return false;
            }
            accumulated = ((accumulated * 10L) - (digit - '0'));
        }
        if (!negative && (accumulated == long.MinValue)) {
            return false;
        }

        value = (negative ? accumulated : -accumulated);

        return true;
    }
}
