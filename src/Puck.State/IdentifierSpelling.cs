namespace Puck.State;

/// <summary>The one lexical identifier rule every Puck source language reads names by: the <c>.puck</c> document
/// grammar, the expression spelling a rule runs, the pattern, cell-set, and state spellings, and the SQL dialect.
/// An identifier opens with an ASCII letter or <c>_</c> and continues with ASCII letters, digits, and <c>_</c>. A
/// name is an identifier, optionally opened by the <see cref="Sigil"/> that marks an engine-reserved name
/// (<c>$value</c>, <c>$tick</c>, <c>$replace</c>); the sigil stands only at the front, never inside a name. Inside a
/// name it marks one Puck generated (<see cref="GeneratedName"/>: <c>turn$east</c>, <c>expect$1</c>), which is never
/// an identifier and which no author-written name may take, bare or quoted.
/// <para>What a context reserves on top — keywords, reserved call names, a pattern's reserved words, the SQL
/// keywords, a dotted path, a reserved channel's <c>:segment</c> continuations — is that context's own named set
/// or walk layered over these classes, never a second character rule. A name outside the rule is still every
/// language's to write: it is quoted, and the quoted spelling reads back the same name. The classes are ASCII so
/// that whether a name prints bare cannot move with the runtime's Unicode tables, and so that two names that look
/// the same are the same characters.</para></summary>
public static class IdentifierSpelling {
    /// <summary>The character that opens an engine-reserved name.</summary>
    public const char Sigil = '$';

    /// <summary>Whether <paramref name="character"/> may open an identifier: an ASCII letter or <c>_</c>.</summary>
    /// <param name="character">The character.</param>
    /// <returns><see langword="true"/> when it opens an identifier.</returns>
    public static bool IsStart(char character) => (char.IsAsciiLetter(c: character) || (character == '_'));
    /// <summary>Whether <paramref name="character"/> may continue an identifier: an ASCII letter, an ASCII digit, or
    /// <c>_</c>.</summary>
    /// <param name="character">The character.</param>
    /// <returns><see langword="true"/> when it continues an identifier.</returns>
    public static bool IsPart(char character) => (char.IsAsciiLetterOrDigit(c: character) || (character == '_'));
    /// <summary>Whether <paramref name="character"/> can stand anywhere in a name: an identifier part or the
    /// <see cref="Sigil"/>. A word whose preceding character is one of these is the tail of a longer name, not a
    /// word of its own.</summary>
    /// <param name="character">The character.</param>
    /// <returns><see langword="true"/> when it can stand in a name.</returns>
    public static bool IsNameCharacter(char character) => (IsPart(character: character) || (character == Sigil));
    /// <summary>Scans one identifier out of <paramref name="text"/> starting at <paramref name="start"/>.</summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="start">The offset to scan from.</param>
    /// <returns>The identifier's length in characters, or 0 when <paramref name="start"/> does not open one.</returns>
    public static int ScanIdentifier(ReadOnlySpan<char> text, int start) {
        if (
            (start < 0) ||
            (start >= text.Length) ||
            !IsStart(character: text[start])
        ) {
            return 0;
        }

        var end = (start + 1);

        while (
            (end < text.Length) &&
            IsPart(character: text[end])
        ) {
            end++;
        }

        return (end - start);
    }
    /// <summary>Scans one name — an identifier, optionally opened by the <see cref="Sigil"/> — out of
    /// <paramref name="text"/> starting at <paramref name="start"/>.</summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="start">The offset to scan from.</param>
    /// <returns>The name's length in characters, or 0 when <paramref name="start"/> does not open one.</returns>
    public static int ScanName(ReadOnlySpan<char> text, int start) {
        if (
            (start < 0) ||
            (start >= text.Length)
        ) {
            return 0;
        }

        if (text[start] != Sigil) {
            return ScanIdentifier(
                start: start,
                text: text
            );
        }

        var identifier = ScanIdentifier(
            start: (start + 1),
            text: text
        );

        return ((identifier == 0)
            ? 0
            : (identifier + 1)
        );
    }
    /// <summary>Whether the whole of <paramref name="text"/> is one identifier.</summary>
    /// <param name="text">The candidate.</param>
    /// <returns><see langword="true"/> when it is one identifier and nothing else.</returns>
    public static bool IsIdentifier(ReadOnlySpan<char> text) => (
        (text.Length > 0) &&
        (ScanIdentifier(
            start: 0,
            text: text
        ) == text.Length)
    );
    /// <summary>Whether the whole of <paramref name="text"/> is one name: an identifier, optionally opened by the
    /// <see cref="Sigil"/>.</summary>
    /// <param name="text">The candidate.</param>
    /// <returns><see langword="true"/> when it is one name and nothing else.</returns>
    public static bool IsName(ReadOnlySpan<char> text) => (
        (text.Length > 0) &&
        (ScanName(
            start: 0,
            text: text
        ) == text.Length)
    );
}
