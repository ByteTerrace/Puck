using System.Globalization;

namespace Puck.Assets.Documents;

/// <summary>The one place a diagnostic's wording is built, so every message reads the same way: it says what is wrong
/// in plain words, names the thing involved, and says how to fix it, in the manner of the Microsoft Learn compiler
/// error pages. A message is one to three sentences, each ending in a period, the offending name in single quotes.
/// The reference page for each code (<c>PuckDiagnosticCodes.HelpPath</c>) carries the longer explanation and a
/// worked example.</summary>
public static class DiagnosticText {
    /// <summary>The most items a message lists by name before it counts the rest.</summary>
    public const int MaxListed = 5;

    /// <summary>Writes a message from what is wrong and, when there is one, how to fix it.</summary>
    /// <param name="problem">What is wrong, as one sentence naming the thing involved.</param>
    /// <param name="fix">How to fix it, as an instruction; <see langword="null"/> when the problem says it all.</param>
    /// <param name="example">The corrected form, or <see langword="null"/>; it follows the fix as <c>For example, ...</c>.</param>
    /// <returns>The sentences joined by a single space, each ended with a period.</returns>
    public static string Compose(string problem, string? fix = null, string? example = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: problem);

        var text = Sentence(text: problem);

        if (!string.IsNullOrWhiteSpace(value: fix)) {
            text = $"{text} {Sentence(text: fix)}";
        }
        if (!string.IsNullOrWhiteSpace(value: example)) {
            text = $"{text} For example, {Sentence(text: example)}";
        }

        return text;
    }
    /// <summary>Writes the message for a value that exceeds a fixed capacity, naming the items that used it.</summary>
    /// <param name="what">What holds the items, such as <c>render.lanes</c>.</param>
    /// <param name="noun">What each item is, in the plural, such as <c>lanes</c>.</param>
    /// <param name="capacity">How many items fit.</param>
    /// <param name="used">The names of the items that were written, in order.</param>
    /// <param name="fix">How to bring the count back within the capacity.</param>
    /// <returns>For example: <c>'render.lanes' holds at most 4 lanes, and 5 are written: 'a', 'b', 'c', 'd' and 'e'. ...</c></returns>
    public static string Capacity(string what, string noun, int capacity, IReadOnlyList<string> used, string fix) {
        ArgumentNullException.ThrowIfNull(argument: used);

        return Compose(
            fix: fix,
            problem: $"'{what}' holds at most {capacity.ToString(provider: CultureInfo.InvariantCulture)} {noun}, and {used.Count.ToString(provider: CultureInfo.InvariantCulture)} are written: {List(items: used)}"
        );
    }
    /// <summary>Spells a short list of names for a message: all of them when few, then a count of the rest.</summary>
    /// <param name="items">The names, in the order they were written.</param>
    /// <returns>For example <c>'a', 'b' and 'c'</c>, or <c>'a', 'b', 'c', 'd', 'e' and 3 more</c>.</returns>
    public static string List(IReadOnlyList<string> items) {
        ArgumentNullException.ThrowIfNull(argument: items);

        var quoted = items.Take(count: MaxListed).Select(selector: static item => $"'{item}'").ToArray();

        if (items.Count > MaxListed) {
            return $"{string.Join(separator: ", ", values: quoted)} and {(items.Count - MaxListed).ToString(provider: CultureInfo.InvariantCulture)} more";
        }

        return (quoted.Length switch {
            0 => "none",
            1 => quoted[0],
            _ => $"{string.Join(separator: ", ", values: quoted[..^1])} and {quoted[^1]}",
        });
    }
    /// <summary>Spells the "what was found" half of a message that names the types of a refused value.</summary>
    /// <param name="noun">The type's name.</param>
    /// <returns>The name with its indefinite article.</returns>
    public static string Article(string noun) => (string.IsNullOrEmpty(value: noun) ? noun : $"{(("aeiouAEIOU".Contains(value: noun[0])) ? "an" : "a")} {noun}");

    // Ends the text with a period unless it already ends a sentence.
    private static string Sentence(string text) {
        text = text.Trim();

        return ((text.Length == 0)
            ? text
            : (text[^1] is '.' or '?' or '!' or ':')
                ? text
                : $"{text}."
        );
    }
}
