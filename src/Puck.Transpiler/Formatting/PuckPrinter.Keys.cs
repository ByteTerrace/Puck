using System.Text;
using Puck.State;

namespace Puck.Transpiler.Formatting;

// A cell key has one spelling wherever it is written: an assignment target's key prints through
// `ExpressionSpelling.AppendSourceKey`, and a key read inside an operand prints through it too. That spelling reprints
// an expression key as the expression reads, parenthesized exactly when the bare text would read back as a simpler
// key: `well[(py + i)]` and `well[py + i]` are one key, printed bare, while `gravity[(level)]` keeps its parentheses,
// since `gravity[level]` names the literal key `level`.
public static partial class PuckPrinter {
    /// <summary>Returns <paramref name="text"/>, an operand as written, with every index key printed the way an
    /// assignment target's key prints (<see cref="ExpressionSpelling.AppendSourceKey"/>). A key that does not parse on
    /// its own, or that holds an interpolated string, which computes its text while lowering, stays as written, and
    /// nothing outside the keys changes.</summary>
    /// <param name="text">The operand text.</param>
    /// <returns>The operand text with its keys in their one spelling.</returns>
    public static string OperandKeys(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        if (!text.Contains(value: '[')) {
            return text;
        }

        var into = new StringBuilder(capacity: text.Length);
        var index = 0;

        AppendRun(end: text.Length, index: ref index, into: into, text: text);

        return into.ToString();
    }

    // Copies text[index..end) into `into`, respelling the key of every index bracket whose target it passes.
    private static void AppendRun(string text, ref int index, int end, StringBuilder into) {
        while (index < end) {
            var character = text[index];

            if ((character is '"' or '`') && TrySkipQuoted(end: end, index: index, next: out var after, text: text)) {
                into.Append(count: (after - index), startIndex: index, value: text);
                index = after;

                continue;
            }
            if ((character == '[') && IsIndexTarget(before: index, text: text) && TryMatch(close: out var close, end: end, open: index, text: text)) {
                var key = new StringBuilder();
                var inner = (index + 1);

                AppendRun(end: close, index: ref inner, into: key, text: text);
                into.Append(value: '[').Append(value: BareKey(key: key.ToString())).Append(value: ']');
                index = (close + 1);

                continue;
            }

            into.Append(value: character);
            index++;
        }
    }
    // The key as an assignment target's key prints, or as written where it does not parse as a key on its own.
    private static string BareKey(string key) {
        var trimmed = key.Trim();

        if (
            (trimmed.Length == 0) ||
            trimmed.Contains(comparisonType: StringComparison.Ordinal, value: "$\"") ||
            !ExpressionSpelling.TryParseKey(error: out _, key: out var parsed, text: trimmed)
        ) {
            return key;
        }

        var spelled = new StringBuilder();

        ExpressionSpelling.AppendSourceKey(into: spelled, key: parsed);

        return spelled.ToString();
    }    // A bracket indexes when it follows a name, a closing bracket or parenthesis, or a backquoted name; anything else
    // opens an array.
    private static bool IsIndexTarget(string text, int before) {
        var previous = (before - 1);

        while ((previous >= 0) && (text[previous] == ' ')) {
            previous--;
        }

        return ((previous >= 0) && (IdentifierSpelling.IsPart(character: text[previous]) || (text[previous] is ']' or ')' or '`')));
    }
    // The bracket or parenthesis closing the one at `open`, stepping over nested pairs and quoted text.
    private static bool TryMatch(string text, int open, int end, out int close) {
        var depth = 0;

        for (var index = open; (index < end); index++) {
            var character = text[index];

            if ((character is '"' or '`') && TrySkipQuoted(end: end, index: index, next: out var after, text: text)) {
                index = (after - 1);

                continue;
            }
            if (character is '[' or '(') {
                depth++;
            } else if (character is ']' or ')') {
                depth--;

                if (depth == 0) {
                    close = index;

                    return true;
                }
            }
        }

        close = -1;

        return false;
    }
    // Steps over a quoted string or a backquoted name that opens at `index`, returning the index just past it.
    private static bool TrySkipQuoted(string text, int index, int end, out int next) {
        var quote = text[index];

        for (var cursor = (index + 1); (cursor < end); cursor++) {
            if ((quote == '"') && (text[cursor] == '\\')) {
                cursor++;

                continue;
            }
            if (text[cursor] == quote) {
                next = (cursor + 1);

                return true;
            }
        }

        next = end;

        return false;
    }
}
