using System.Globalization;

namespace Puck.Text;

/// <summary>
/// Parses and expands the textual code-point range tokens carried by
/// <see cref="FontAtlasGenerationOptions.AllowedCodePointRanges"/> into a set of Unicode scalar values.
/// </summary>
/// <remarks>
/// A token is one of: a single code point such as <c>U+0041</c> (the <c>U+</c> prefix is optional and the
/// value is hexadecimal); an inclusive range such as <c>U+0020-U+007E</c>; or the wildcard <c>*</c>, which
/// requests every Basic Multilingual Plane code point. Within a single entry, tokens may be separated by
/// commas, semicolons, or whitespace. Surrogate code points (<c>U+D800</c>–<c>U+DFFF</c>) and values above
/// <c>U+10FFFF</c> are rejected because they are not valid Unicode scalar values.
/// </remarks>
public static class UnicodeCodePointRangeExpander {
    private static int ParseCodePoint(string token) {
        var normalized = (token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "U+"
        )
            ? token[2..]
            : token);

        if (!int.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out var codePoint,
            s: normalized,
            style: NumberStyles.AllowHexSpecifier
        )) {
            throw new ArgumentException(message: $"Unsupported allowed code point token '{token}'. Use U+XXXX or U+XXXX-U+YYYY, or '*' to probe all BMP code points (U+0000-U+FFFF).");
        }

        if (codePoint > 0x10FFFF) {
            throw new ArgumentException(message: $"Code point '{token}' exceeded the Unicode maximum U+10FFFF.");
        }

        if (codePoint is >= 0xD800 and <= 0xDFFF) {
            throw new ArgumentException(message: $"Code point '{token}' is within UTF-16 surrogate space (U+D800-U+DFFF) and is not a valid Unicode scalar value.");
        }

        return codePoint;
    }
    private static bool RangeIncludesSurrogates(int startCodePoint, int endCodePoint) {
        return (
            (startCodePoint <= 0xDFFF) &&
            (endCodePoint >= 0xD800)
        );
    }

    /// <summary>Enumerates every Unicode scalar value in the Basic Multilingual Plane.</summary>
    /// <returns>The code points from <c>U+0000</c> through <c>U+FFFF</c>, excluding the surrogate range <c>U+D800</c>–<c>U+DFFF</c>.</returns>
    /// <remarks>This is the expansion applied when a caller observes a wildcard via <see cref="Expand(IReadOnlyList{string}, out bool)"/>.</remarks>
    public static IEnumerable<int> EnumerateBmpCodePoints() {
        for (var codePoint = 0; (codePoint <= 0xFFFF); codePoint++) {
            if (codePoint is >= 0xD800 and <= 0xDFFF) {
                continue;
            }

            yield return codePoint;
        }
    }
    /// <summary>Expands a list of range entries into the distinct set of Unicode scalar values they name.</summary>
    /// <param name="ranges">The range entries to parse. Empty or whitespace entries are ignored.</param>
    /// <param name="wildcardSelected">When this method returns, <see langword="true"/> if any entry contained the <c>*</c> wildcard; otherwise <see langword="false"/>. The wildcard is not itself expanded — callers that observe it typically add <see cref="EnumerateBmpCodePoints"/> to the result.</param>
    /// <returns>The set of code points named explicitly or by range, excluding the wildcard.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ranges"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A token is malformed, a range start exceeds its end, a value exceeds <c>U+10FFFF</c>, or a value or range includes a UTF-16 surrogate code point.</exception>
    public static HashSet<int> Expand(IReadOnlyList<string> ranges, out bool wildcardSelected) {
        ArgumentNullException.ThrowIfNull(ranges);

        wildcardSelected = false;
        var expanded = new HashSet<int>();

        foreach (var entry in ranges) {
            if (string.IsNullOrWhiteSpace(value: entry)) {
                continue;
            }

            var tokens = entry.Split(
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                separator: [',', ';', ' ', '\t', '\r', '\n']
            );

            foreach (var token in tokens) {
                if (token == "*") {
                    wildcardSelected = true;
                    continue;
                }

                if (token.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: '-'
                )) {
                    var separatorIndex = token.IndexOf(
                        comparisonType: StringComparison.Ordinal,
                        value: '-'
                    );
                    var startCodePoint = ParseCodePoint(token: token[..separatorIndex]);
                    var endCodePoint = ParseCodePoint(token: token[(separatorIndex + 1)..]);

                    if (startCodePoint > endCodePoint) {
                        throw new ArgumentException(message: $"Range '{token}' had a start value greater than its end value.");
                    }

                    if (RangeIncludesSurrogates(
                        endCodePoint: endCodePoint,
                        startCodePoint: startCodePoint
                    )) {
                        throw new ArgumentException(message: $"Range '{token}' included UTF-16 surrogate code points (U+D800-U+DFFF), which are not valid Unicode scalar values.");
                    }

                    for (var codePoint = startCodePoint; (codePoint <= endCodePoint); codePoint++) {
                        expanded.Add(item: codePoint);
                    }

                    continue;
                }

                expanded.Add(item: ParseCodePoint(token: token));
            }
        }

        return expanded;
    }
}
