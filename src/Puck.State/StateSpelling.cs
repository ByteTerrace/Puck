using System.Globalization;
using System.Text;
using Puck.Maths;

namespace Puck.State;

/// <summary>The authored spelling of a state enum token, as a refusal message must render it.</summary>
/// <remarks>A refusal quotes the token the author wrote in the document, not the CLR name — one home so a validator
/// refusal and a runtime refusal about the same value never disagree on how it is spelled. The infix grammars
/// (<see cref="CellSetSpelling"/>, <see cref="PatternSpelling"/>) share their print and parse skeleton here, over one
/// <see cref="SpellingCursor"/>.</remarks>
public static class StateSpelling {
    /// <summary>The document spelling of a <see cref="Puck.State.CycleOutput"/> — the enum's own name, as the strict
    /// enum converter reads and writes it.</summary>
    /// <param name="output">The cycle output.</param>
    /// <returns>The output's authored token.</returns>
    public static string CycleOutput(CycleOutput output) => output.ToString();
    /// <summary>Describes the authored spelling of a generator source shape.</summary>
    /// <param name="source">The source shape.</param>
    /// <returns>The source's authored token.</returns>
    public static string GeneratorSource(GeneratorSource source) =>
        (char.ToLowerInvariant(c: source.ToString()[0]) + source.ToString()[1..]);
    /// <summary>Describes the authored spelling of a cell kind — the declared member name, as the strict enum
    /// converter reads and writes it.</summary>
    /// <param name="kind">The cell kind.</param>
    /// <returns>The kind's authored token.</returns>
    public static string Kind(CellKind kind) => kind.ToString();
    /// <summary>Describes one cell value as a read-back renders it, with one spelling per case.</summary>
    /// <param name="value">The value to spell.</param>
    /// <param name="symbols">The enum the value's row draws its cells from, or <see langword="null"/> for a row that
    /// names none; an Int value naming one of its members is spelled by the member's name.</param>
    /// <returns>The value's read-back spelling.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> holds no case.</exception>
    /// <remarks>A vector is spelled by its width and digest rather than its components: a read-back line is for a
    /// person, and the components are read with the vector verbs.</remarks>
    public static string Value(CellValue value, StateEnum? symbols = null) => value.Kind switch {
        CellKind.Int when ((symbols is not null) && symbols.TryGetMember(
            member: out var member,
            value: value.AsInt
        )) => member.Value,
        CellKind.Int => value.AsInt.ToString(provider: CultureInfo.InvariantCulture),
        CellKind.Fixed => FixedQ4816.FromRawBits(value: value.AsFixed).ToString(),
        CellKind.Bool => (value.AsBool
            ? "true"
            : "false"),
        CellKind.Text => $"'{value.AsText}'",
        CellKind.Vector => $"vector[{value.AsVector.Length.ToString(provider: CultureInfo.InvariantCulture)}] #{StateVector.ComputeDigest(components: value.AsVector.Span):x8}",
        _ => throw new InvalidOperationException(message: $"Unknown cell kind '{value.Kind}'."),
    };

    /// <summary>Quotes a name the bare spelling cannot carry, escaping each backslash and quote with a backslash, so
    /// <see cref="SpellingCursor.TryReadName"/> reads back exactly the name quoted.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The quoted spelling.</returns>
    internal static string QuoteName(string name) => $"\"{name.Replace(
        newValue: "\\\\",
        oldValue: "\\"
    ).Replace(
        newValue: "\\\"",
        oldValue: "\""
    )}\"";

    /// <summary>Reads one node of an infix spelling.</summary>
    internal delegate bool SpellingRead<T>(ref SpellingCursor cursor, out T? node, out string error) where T : class;
    /// <summary>Takes the token that joins two items of an infix list, reporting whether one was there.</summary>
    internal delegate bool SpellingJoin(ref SpellingCursor cursor);
    /// <summary>Prints a node, parenthesized when it binds looser than the level its position requires.</summary>
    internal delegate bool SpellingPrint<T>(T? node, int minimumLevel, StringBuilder text) where T : class;
    internal delegate int LevelOfDelegate<T>(T node);
    internal delegate bool TryPrintBodyDelegate<T>(T node, StringBuilder text);

    /// <summary>Prints a node with parentheses when its precedence level is below the minimum.</summary>
    internal static bool TryPrintParenthesized<T>(
        LevelOfDelegate<T> levelOf,
        int minimumLevel,
        T? node,
        StringBuilder text,
        TryPrintBodyDelegate<T> tryPrintBody
    ) where T : class {
        if (node is null) {
            return false;
        }

        var parenthesize = (levelOf(node) < minimumLevel);

        if (parenthesize) {
            _ = text.Append(value: '(');
        }

        if (!tryPrintBody(node, text)) {
            return false;
        }

        if (parenthesize) {
            _ = text.Append(value: ')');
        }

        return true;
    }
    /// <summary>Prints a whole node, or reports that some part of it has no spelling.</summary>
    internal static bool TryPrint<T>(T? node, int minimumLevel, SpellingPrint<T> print, out string text) where T : class {
        var builder = new StringBuilder();

        if (!print(
            minimumLevel: minimumLevel,
            node: node,
            text: builder
        )) {
            text = string.Empty;

            return false;
        }

        text = builder.ToString();

        return true;
    }
    /// <summary>Prints an n-ary node's items joined by its separator. A list shorter than two items has no spelling,
    /// since once printed it reads back as its one item.</summary>
    internal static bool TryPrintInfixList<T>(IReadOnlyList<T>? items, string separator, int minimumLevel, StringBuilder text, SpellingPrint<T> printItem) where T : class {
        if (items is not { Count: > 1 }) {
            return false;
        }

        for (var index = 0; (index < items.Count); index++) {
            if (index > 0) {
                _ = text.Append(value: separator);
            }
            if (!printItem(
                minimumLevel: minimumLevel,
                node: items[index],
                text: text
            )) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Parses a whole text through its root reader, refusing an empty text and anything left unread.</summary>
    internal static bool TryParse<T>(string? text, string emptyError, string domain, SpellingRead<T> readRoot, out T? node, out string error) where T : class {
        node = null;

        if (string.IsNullOrWhiteSpace(value: text)) {
            error = emptyError;

            return false;
        }

        var cursor = new SpellingCursor(text: text);

        if (
            !readRoot(
                cursor: ref cursor,
                error: out error,
                node: out node
            ) ||
            !cursor.TryReadEnd(
                domain: domain,
                error: out error
            )
        ) {
            node = null;

            return false;
        }

        return true;
    }
    /// <summary>Reads one item, then one more after each join, folding two or more into a single node.</summary>
    internal static bool TryReadInfixList<T>(ref SpellingCursor cursor, SpellingJoin join, SpellingRead<T> readItem, Func<List<T>, T> combine, out T? node, out string error) where T : class {
        if (!readItem(
            cursor: ref cursor,
            error: out error,
            node: out node
        )) {
            return false;
        }

        List<T>? items = null;

        while (join(cursor: ref cursor)) {
            if (!readItem(
                cursor: ref cursor,
                error: out error,
                node: out var item
            )) {
                node = null;

                return false;
            }

            items ??= [node!];
            items.Add(item: item!);
        }

        if (items is not null) {
            node = combine(arg: items);
        }

        return true;
    }
}
