using System.Globalization;
using System.Text;

namespace Puck.State;

/// <summary>The infix front end for <see cref="PatternNode"/>: the text a <c>.puck</c> source writes a pattern's
/// language in, and the text a decompiler prints it back as.</summary>
/// <remarks>The operators, loosest first: <c>|</c> choice, <c>&amp;</c> intersection, juxtaposition sequence,
/// <c>~</c> complement, and the postfix repetitions <c>*</c>, <c>+</c>, <c>?</c>, <c>{m}</c>, <c>{m, n}</c>. The
/// atoms are <c>any</c> (one token of any value), <c>empty</c> (the empty word), <c>none</c> (no word at all),
/// <c>except(sym)</c>, and a symbol name — quoted when it collides with one of those words or is not a bare
/// identifier. <see cref="Print"/> is the inverse of <see cref="TryParse"/> over every node
/// <see cref="TryPrint"/> accepts, and a node it refuses is one no source text spells: an n-ary node carrying
/// fewer than two items, which is indistinguishable from its one item or from an atom once printed.</remarks>
public static class PatternSpelling {
    private const int BothLevel = 2;
    private const int ChoiceLevel = 1;
    private const int ComplementLevel = 4;
    private const int LeafLevel = 6;
    private const string NameDomain = "symbol name";
    private const int RepetitionLevel = 5;
    private const int SequenceLevel = 3;

    /// <summary>Gets the words a pattern reads as atoms rather than symbols, so a symbol spelling one prints
    /// quoted.</summary>
    public static IReadOnlySet<string> ReservedWords { get; } = System.Collections.Frozen.FrozenSet.ToFrozenSet(
        comparer: StringComparer.Ordinal,
        source: ["any", "empty", "except", "none"]
    );

    private static int LevelOf(PatternNode node) => node switch {
        PatternNode.Choice => ChoiceLevel,
        PatternNode.Both => BothLevel,
        PatternNode.Sequence => SequenceLevel,
        PatternNode.Complement => ComplementLevel,
        PatternNode.Optional or PatternNode.Star or PatternNode.Plus or PatternNode.Repeat => RepetitionLevel,
        _ => LeafLevel,
    };
    private static bool IsBareIdentifier(string name) => (
        IdentifierSpelling.IsIdentifier(text: name) &&
        !ReservedWords.Contains(item: name)
    );
    private static string SymbolText(string name) => (IsBareIdentifier(name: name)
        ? name
        : StateSpelling.QuoteName(name: name)
    );
    private static bool TryPrintInto(PatternNode? node, int minimumLevel, StringBuilder text) =>
        StateSpelling.TryPrintParenthesized(
            levelOf: LevelOf,
            minimumLevel: minimumLevel,
            node: node,
            text: text,
            tryPrintBody: TryPrintBody
        );
    private static bool TryPrintBody(PatternNode node, StringBuilder text) {
        switch (node) {
            case PatternNode.Symbol symbol: {
                    // A symbol with no name has no spelling the parser reads back.
                    if (string.IsNullOrEmpty(value: symbol.Name)) {
                        return false;
                    }

                    _ = text.Append(value: SymbolText(name: symbol.Name));

                    return true;
                }
            case PatternNode.AnySymbol: {
                    _ = text.Append(value: "any");

                    return true;
                }
            case PatternNode.Except except: {
                    _ = text.Append(value: "except(").Append(value: SymbolText(name: except.Name)).Append(value: ')');

                    return true;
                }
            case PatternNode.Nothing: {
                    _ = text.Append(value: "empty");

                    return true;
                }
            case PatternNode.None: {
                    _ = text.Append(value: "none");

                    return true;
                }
            case PatternNode.Sequence sequence: {
                    return StateSpelling.TryPrintInfixList(
                        items: sequence.Items,
                        minimumLevel: ComplementLevel,
                        printItem: TryPrintInto,
                        separator: " ",
                        text: text
                    );
                }
            case PatternNode.Choice choice: {
                    return StateSpelling.TryPrintInfixList(
                        items: choice.Items,
                        minimumLevel: BothLevel,
                        printItem: TryPrintInto,
                        separator: " | ",
                        text: text
                    );
                }
            case PatternNode.Both both: {
                    return StateSpelling.TryPrintInfixList(
                        items: both.Items,
                        minimumLevel: SequenceLevel,
                        printItem: TryPrintInto,
                        separator: " & ",
                        text: text
                    );
                }
            case PatternNode.Complement complement: {
                    _ = text.Append(value: '~');

                    return TryPrintInto(
                        minimumLevel: ComplementLevel,
                        node: complement.Item,
                        text: text
                    );
                }
            case PatternNode.Optional optional: {
                    return TryPrintRepetition(
                        item: optional.Item,
                        suffix: "?",
                        text: text
                    );
                }
            case PatternNode.Star star: {
                    return TryPrintRepetition(
                        item: star.Item,
                        suffix: "*",
                        text: text
                    );
                }
            case PatternNode.Plus plus: {
                    return TryPrintRepetition(
                        item: plus.Item,
                        suffix: "+",
                        text: text
                    );
                }
            case PatternNode.Repeat repeat: {
                    return TryPrintRepetition(
                        item: repeat.Item,
                        suffix: ((repeat.Min == repeat.Max)
                            ? $"{{{repeat.Min.ToString(provider: CultureInfo.InvariantCulture)}}}"
                            : $"{{{repeat.Min.ToString(provider: CultureInfo.InvariantCulture)}, {repeat.Max.ToString(provider: CultureInfo.InvariantCulture)}}}"
                        ),
                        text: text
                    );
                }
            default: {
                    return false;
                }
        }
    }
    private static bool TryPrintRepetition(PatternNode? item, string suffix, StringBuilder text) {
        if (!TryPrintInto(
            minimumLevel: RepetitionLevel,
            node: item,
            text: text
        )) {
            return false;
        }
        _ = text.Append(value: suffix);

        return true;
    }

    /// <summary>Returns the canonical text a node prints as.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The node carries an n-ary item list shorter than two entries, which no
    /// text spells.</exception>
    public static string Print(PatternNode node) {
        ArgumentNullException.ThrowIfNull(argument: node);

        return (TryPrint(
            node: node,
            text: out var text
        )
            ? text
            : throw new ArgumentException(
                message: "a sequence, choice, or intersection carrying fewer than two items has no spelling",
                paramName: nameof(node)
            )
        );
    }
    /// <summary>Prints a node as text when every part of it has a spelling.</summary>
    /// <param name="node">The node.</param>
    /// <param name="text">The text, or empty when the node has no spelling.</param>
    /// <returns><see langword="true"/> when the node printed.</returns>
    public static bool TryPrint(PatternNode? node, out string text) =>
        StateSpelling.TryPrint(
            minimumLevel: ChoiceLevel,
            node: node,
            print: TryPrintInto,
            text: out text
        );
    /// <summary>Parses pattern text into a node.</summary>
    /// <param name="text">The text.</param>
    /// <param name="node">The node, on success.</param>
    /// <param name="error">Why the text was refused, or empty on success; never empty on a refusal.</param>
    /// <returns><see langword="true"/> when the text parsed.</returns>
    public static bool TryParse(string? text, out PatternNode? node, out string error) =>
        StateSpelling.TryParse(
            domain: "pattern",
            emptyError: "is empty; a pattern names at least one symbol",
            error: out error,
            node: out node,
            readRoot: TryReadChoice,
            text: text
        );

    private static bool TryReadChoice(ref SpellingCursor cursor, out PatternNode? node, out string error) =>
        StateSpelling.TryReadInfixList<PatternNode>(
            combine: static items => new PatternNode.Choice(Items: items),
            cursor: ref cursor,
            error: out error,
            join: static (ref SpellingCursor next) => next.TryTake(c: '|'),
            node: out node,
            readItem: TryReadBoth
        );
    private static bool TryReadBoth(ref SpellingCursor cursor, out PatternNode? node, out string error) =>
        StateSpelling.TryReadInfixList<PatternNode>(
            combine: static items => new PatternNode.Both(Items: items),
            cursor: ref cursor,
            error: out error,
            join: static (ref SpellingCursor next) => next.TryTake(c: '&'),
            node: out node,
            readItem: TryReadSequence
        );
    // Juxtaposition joins a sequence: the next item follows whenever what comes next opens an atom.
    private static bool TryReadSequence(ref SpellingCursor cursor, out PatternNode? node, out string error) =>
        StateSpelling.TryReadInfixList<PatternNode>(
            combine: static items => new PatternNode.Sequence(Items: items),
            cursor: ref cursor,
            error: out error,
            join: static (ref SpellingCursor next) => (
                next.TryPeek(c: out var c) &&
                (
                    (c is '(' or '~' or '"') ||
                    IdentifierSpelling.IsStart(character: c)
                )
            ),
            node: out node,
            readItem: TryReadUnary
        );
    private static bool TryReadUnary(ref SpellingCursor cursor, out PatternNode? node, out string error) {
        if (!cursor.TryTake(c: '~')) {
            return TryReadRepetition(
                cursor: ref cursor,
                error: out error,
                node: out node
            );
        }
        if (!TryReadUnary(
            cursor: ref cursor,
            error: out error,
            node: out var item
        )) {
            node = null;

            return false;
        }

        node = new PatternNode.Complement(Item: item!);

        return true;
    }
    private static bool TryReadRepetition(ref SpellingCursor cursor, out PatternNode? node, out string error) {
        if (!TryReadAtom(
            cursor: ref cursor,
            error: out error,
            node: out node
        )) {
            return false;
        }

        while (true) {
            if (cursor.TryTake(c: '*')) {
                node = new PatternNode.Star(Item: node!);

                continue;
            }
            if (cursor.TryTake(c: '+')) {
                node = new PatternNode.Plus(Item: node!);

                continue;
            }
            if (cursor.TryTake(c: '?')) {
                node = new PatternNode.Optional(Item: node!);

                continue;
            }
            if (!cursor.TryTake(c: '{')) {
                return true;
            }
            if (!TryReadCount(
                count: out var minimum,
                cursor: ref cursor,
                error: out error
            )) {
                node = null;

                return false;
            }

            var maximum = minimum;

            if (cursor.TryTake(c: ',') && !TryReadCount(
                count: out maximum,
                cursor: ref cursor,
                error: out error
            )) {
                node = null;

                return false;
            }
            if (!cursor.TryTake(c: '}')) {
                error = "expects '}' closing a repetition count";
                node = null;

                return false;
            }
            if (maximum < minimum) {
                error = $"repeats {minimum}..{maximum} times, which is not least first";
                node = null;

                return false;
            }
            if (maximum > PatternCapacity.MaxRepeat) {
                error = $"repeats {maximum} times, past the {PatternCapacity.MaxRepeat} a repetition unrolls";
                node = null;

                return false;
            }

            node = new PatternNode.Repeat(
                Item: node!,
                Max: maximum,
                Min: minimum
            );
        }
    }
    private static bool TryReadAtom(ref SpellingCursor cursor, out PatternNode? node, out string error) {
        node = null;
        error = string.Empty;

        if (cursor.TryTake(c: '(')) {
            if (!TryReadChoice(
                cursor: ref cursor,
                error: out error,
                node: out node
            )) {
                return false;
            }
            if (!cursor.TryTake(c: ')')) {
                error = "expects ')' closing a parenthesized pattern";
                node = null;

                return false;
            }

            return true;
        }
        if (cursor.TryTakeWord(word: "any")) {
            node = new PatternNode.AnySymbol();

            return true;
        }
        if (cursor.TryTakeWord(word: "empty")) {
            node = new PatternNode.Nothing();

            return true;
        }
        if (cursor.TryTakeWord(word: "none")) {
            node = new PatternNode.None();

            return true;
        }
        if (cursor.TryTakeWord(word: "except")) {
            if (!cursor.TryTake(c: '(')) {
                error = "expects '(' after 'except'";

                return false;
            }
            if (!cursor.TryReadName(
                domain: NameDomain,
                error: out error,
                name: out var excluded
            )) {
                return false;
            }
            if (!cursor.TryTake(c: ')')) {
                error = "expects ')' closing 'except'";

                return false;
            }

            node = new PatternNode.Except(Name: excluded);

            return true;
        }
        if (!cursor.TryReadName(
            domain: NameDomain,
            error: out error,
            name: out var symbol
        )) {
            return false;
        }

        node = new PatternNode.Symbol(Name: symbol);

        return true;
    }
    private static bool TryReadCount(ref SpellingCursor cursor, out int count, out string error) {
        if (
            !cursor.TryReadWholeNumber(
                signed: false,
                value: out var whole
            ) ||
            (whole > int.MaxValue)
        ) {
            count = 0;
            error = "expects a whole repetition count";

            return false;
        }

        count = ((int)whole);
        error = string.Empty;

        return true;
    }
}
