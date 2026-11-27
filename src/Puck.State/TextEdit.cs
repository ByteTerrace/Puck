using System.Text;

namespace Puck.State;

/// <summary>
/// Edits the end of a text by a whole number, the meaning an add to a kind=Text cell carries: a positive number appends
/// the Unicode scalar it names, a negative one removes that many scalars from the end (fewer when the text holds fewer),
/// and zero leaves the text as it stands. Scalars are counted whole, so a removal never splits a surrogate pair.
/// </summary>
public static class TextEdit {
    /// <summary>Applies an edit to a text.</summary>
    /// <param name="text">The text to edit.</param>
    /// <param name="edit">The scalar value to append when positive, or the negated count of scalars to remove.</param>
    /// <param name="edited">The edited text, or <paramref name="text"/> itself when the edit changes nothing.</param>
    /// <returns><see langword="false"/> when <paramref name="edit"/> is positive but names no Unicode scalar (past
    /// U+10FFFF, or a surrogate code point); <paramref name="edited"/> is then <paramref name="text"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static bool TryApply(string text, long edit, out string edited) {
        ArgumentNullException.ThrowIfNull(argument: text);

        edited = text;

        if (edit > 0L) {
            if ((edit > int.MaxValue) || !Rune.IsValid(value: ((int)edit))) {
                return false;
            }

            edited = string.Concat(str0: text, str1: new Rune(value: ((int)edit)).ToString());

            return true;
        }

        var end = text.Length;

        for (var removed = 0L; ((removed > edit) && (end > 0)); removed--) {
            _ = Rune.DecodeLastFromUtf16(source: text.AsSpan(start: 0, length: end), result: out _, charsConsumed: out var consumed);
            end -= consumed;
        }

        if (end < text.Length) {
            edited = text[..end];
        }

        return true;
    }
}
