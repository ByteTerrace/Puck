using System.Globalization;
using System.Text;

namespace Puck.State;

/// <summary>Reads one infix spelling left to right: the one cursor <see cref="CellSetSpelling"/> and
/// <see cref="PatternSpelling"/> parse through, so the two grammars skip space, take a token, read a name, and read
/// a whole number the same way.</summary>
/// <param name="text">The text being read.</param>
internal ref struct SpellingCursor(string text) {
    private readonly string m_text = text;
    private int m_offset = 0;

    /// <summary>Gets the text not yet read, as a refusal quotes it.</summary>
    public readonly string Rest => m_text[m_offset..];

    /// <summary>Advances past any whitespace.</summary>
    public void SkipSpace() {
        while (
            (m_offset < m_text.Length) &&
            char.IsWhiteSpace(c: m_text[m_offset])
        ) {
            m_offset++;
        }
    }
    /// <summary>Skips whitespace and reports the next character without taking it.</summary>
    /// <param name="c">The next character, when one remains.</param>
    /// <returns><see langword="true"/> when a character remains.</returns>
    public bool TryPeek(out char c) {
        SkipSpace();

        if (m_offset >= m_text.Length) {
            c = default;

            return false;
        }

        c = m_text[m_offset];

        return true;
    }
    /// <summary>Skips whitespace and takes one character when it is next.</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> when the character was taken.</returns>
    public bool TryTake(char c) {
        SkipSpace();

        return TryTakeImmediate(c: c);
    }
    /// <summary>Takes one character when it is next, without skipping whitespace first.</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> when the character was taken.</returns>
    public bool TryTakeImmediate(char c) {
        if (
            (m_offset >= m_text.Length) ||
            (m_text[m_offset] != c)
        ) {
            return false;
        }

        m_offset++;

        return true;
    }
    /// <summary>Skips whitespace and takes a word when it is next and ends at a word boundary.</summary>
    /// <param name="word">The word.</param>
    /// <returns><see langword="true"/> when the word was taken.</returns>
    public bool TryTakeWord(string word) {
        SkipSpace();

        var end = (m_offset + word.Length);

        if (
            (end > m_text.Length) ||
            !m_text.AsSpan(
                length: word.Length,
                start: m_offset
            ).SequenceEqual(other: word) ||
            (
                (end < m_text.Length) &&
                IdentifierSpelling.IsPart(character: m_text[end])
            )
        ) {
            return false;
        }

        m_offset = end;

        return true;
    }
    /// <summary>Skips whitespace and reads a name: a bare identifier, or a quoted name whose backslash takes the
    /// next character literally, the inverse of <see cref="StateSpelling.QuoteName"/>.</summary>
    /// <param name="domain">What the name names, as a refusal says it (<c>row name</c>, <c>symbol name</c>).</param>
    /// <param name="name">The name read.</param>
    /// <param name="error">Why no name was read, or empty on success.</param>
    /// <returns><see langword="true"/> when a non-empty name was read.</returns>
    public bool TryReadName(string domain, out string name, out string error) {
        SkipSpace();

        name = string.Empty;
        error = string.Empty;

        if (TryTakeImmediate(c: '"')) {
            var quoted = new StringBuilder();

            while (
                (m_offset < m_text.Length) &&
                (m_text[m_offset] != '"')
            ) {
                if (
                    (m_text[m_offset] == '\\') &&
                    ((m_offset + 1) < m_text.Length)
                ) {
                    m_offset++;
                }

                _ = quoted.Append(value: m_text[m_offset]);
                m_offset++;
            }

            if (!TryTakeImmediate(c: '"')) {
                error = $"expects '\"' closing a {domain}";

                return false;
            }
            if (quoted.Length == 0) {
                error = $"expects a {domain} between the quotes";

                return false;
            }

            name = quoted.ToString();

            return true;
        }

        var start = m_offset;

        m_offset += IdentifierSpelling.ScanIdentifier(
            start: m_offset,
            text: m_text
        );

        if (m_offset == start) {
            error = $"expects a {domain} at '{Rest}'";

            return false;
        }

        name = m_text[start..m_offset];

        return true;
    }
    /// <summary>Skips whitespace and reads a run of decimal digits, optionally signed, as a whole number. On failure
    /// the cursor stands where the number was expected, so <see cref="Rest"/> quotes what was found there.</summary>
    /// <param name="signed">Whether a leading <c>-</c> belongs to the number.</param>
    /// <param name="value">The number read.</param>
    /// <returns><see langword="true"/> when a number that fits a <see cref="long"/> was read.</returns>
    public bool TryReadWholeNumber(bool signed, out long value) {
        SkipSpace();

        var start = m_offset;

        if (signed) {
            _ = TryTakeImmediate(c: '-');
        }

        while (
            (m_offset < m_text.Length) &&
            char.IsAsciiDigit(c: m_text[m_offset])
        ) {
            m_offset++;
        }

        if (long.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out value,
            s: m_text.AsSpan(
                length: (m_offset - start),
                start: start
            ),
            style: NumberStyles.AllowLeadingSign
        )) {
            return true;
        }

        m_offset = start;

        return false;
    }
    /// <summary>Verifies the whole text was read.</summary>
    /// <param name="domain">What the text spells, as a refusal says it (<c>cell set</c>, <c>pattern</c>).</param>
    /// <param name="error">Why the text was refused, or empty when it was read to its end.</param>
    /// <returns><see langword="true"/> when nothing but whitespace remains.</returns>
    public bool TryReadEnd(string domain, out string error) {
        SkipSpace();

        if (m_offset < m_text.Length) {
            error = $"carries '{Rest}' after the {domain}";

            return false;
        }

        error = string.Empty;

        return true;
    }
}
