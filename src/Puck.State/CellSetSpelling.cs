using System.Globalization;
using System.Text;

namespace Puck.State;

/// <summary>The infix front end for <see cref="CellSetExpression"/>: the same operator vocabulary
/// <see cref="PatternSpelling"/> writes over a word, written over the positions of a family, a board, or a
/// zone.</summary>
/// <remarks>The operators, loosest first: <c>|</c> union, <c>&amp;</c> intersection, <c>~</c> complement. The atoms
/// are <c>all</c>, <c>none</c>, and the three sources <c>board(row, low..high)</c>,
/// <c>family(name, low..high)</c>, and <c>zone(row, low..high)</c>, whose range is the inclusive band a position's
/// value falls in to be a member. <see cref="Print"/> is the inverse of <see cref="TryParse"/> over every
/// expression <see cref="TryPrint"/> accepts, and the one it refuses is a union or intersection carrying fewer than
/// two items, which is indistinguishable from its one item once printed.</remarks>
public static class CellSetSpelling {
    private const int AnyLevel = 1;
    private const int BothLevel = 2;
    private const int ComplementLevel = 3;
    private const int LeafLevel = 4;
    private const string NameDomain = "row name";

    private static int LevelOf(CellSetExpression expression) => expression switch {
        CellSetExpression.Any => AnyLevel,
        CellSetExpression.Both => BothLevel,
        CellSetExpression.Complement => ComplementLevel,
        _ => LeafLevel,
    };
    private static string NameText(CellName name) => (IdentifierSpelling.IsIdentifier(text: name.Value)
        ? name.Value
        : StateSpelling.QuoteName(name: name.Value)
    );
    private static string SourceText(string kind, CellName name, long low, long high) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{kind}({NameText(name: name)}, {low}..{high})"
    );
    private static bool TryPrintInto(CellSetExpression? expression, int minimumLevel, StringBuilder text) =>
        StateSpelling.TryPrintParenthesized(
            levelOf: LevelOf,
            minimumLevel: minimumLevel,
            node: expression,
            text: text,
            tryPrintBody: TryPrintBody
        );
    private static bool TryPrintBody(CellSetExpression expression, StringBuilder text) {
        switch (expression) {
            case CellSetExpression.Board board: {
                    _ = text.Append(value: SourceText(
                        high: board.High,
                        kind: "board",
                        low: board.Low,
                        name: board.Row
                    ));

                    return true;
                }
            case CellSetExpression.Family family: {
                    _ = text.Append(value: SourceText(
                        high: family.High,
                        kind: "family",
                        low: family.Low,
                        name: family.Name
                    ));

                    return true;
                }
            case CellSetExpression.Zone zone: {
                    _ = text.Append(value: SourceText(
                        high: zone.High,
                        kind: "zone",
                        low: zone.Low,
                        name: zone.Row
                    ));

                    return true;
                }
            case CellSetExpression.Everything: {
                    _ = text.Append(value: "all");

                    return true;
                }
            case CellSetExpression.Nothing: {
                    _ = text.Append(value: "none");

                    return true;
                }
            case CellSetExpression.Any any: {
                    return StateSpelling.TryPrintInfixList(
                        items: any.Items,
                        minimumLevel: BothLevel,
                        printItem: TryPrintInto,
                        separator: " | ",
                        text: text
                    );
                }
            case CellSetExpression.Both both: {
                    return StateSpelling.TryPrintInfixList(
                        items: both.Items,
                        minimumLevel: ComplementLevel,
                        printItem: TryPrintInto,
                        separator: " & ",
                        text: text
                    );
                }
            case CellSetExpression.Complement complement: {
                    _ = text.Append(value: '~');

                    return TryPrintInto(
                        expression: complement.Item,
                        minimumLevel: ComplementLevel,
                        text: text
                    );
                }
            default: {
                    return false;
                }
        }
    }

    /// <summary>Returns the canonical text an expression prints as.</summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The expression carries a union or intersection shorter than two items,
    /// which no text spells.</exception>
    public static string Print(CellSetExpression expression) {
        ArgumentNullException.ThrowIfNull(argument: expression);

        return (TryPrint(
            expression: expression,
            text: out var text
        )
            ? text
            : throw new ArgumentException(
                message: "a union or intersection carrying fewer than two items has no spelling",
                paramName: nameof(expression)
            )
        );
    }
    /// <summary>Prints an expression as text when every part of it has a spelling.</summary>
    /// <param name="expression">The expression.</param>
    /// <param name="text">The text, or empty when the expression has no spelling.</param>
    /// <returns><see langword="true"/> when the expression printed.</returns>
    public static bool TryPrint(CellSetExpression? expression, out string text) =>
        StateSpelling.TryPrint(
            minimumLevel: AnyLevel,
            node: expression,
            print: TryPrintInto,
            text: out text
        );
    /// <summary>Parses cell-set text into an expression.</summary>
    /// <param name="text">The text.</param>
    /// <param name="expression">The expression, on success.</param>
    /// <param name="error">Why the text was refused, or empty on success; never empty on a refusal.</param>
    /// <returns><see langword="true"/> when the text parsed.</returns>
    public static bool TryParse(string? text, out CellSetExpression? expression, out string error) =>
        StateSpelling.TryParse(
            domain: "cell set",
            emptyError: "is empty; a cell set names a board, family, or zone",
            error: out error,
            node: out expression,
            readRoot: TryReadAny,
            text: text
        );

    private static bool TryReadAny(ref SpellingCursor cursor, out CellSetExpression? expression, out string error) =>
        StateSpelling.TryReadInfixList<CellSetExpression>(
            combine: static items => new CellSetExpression.Any(Items: items),
            cursor: ref cursor,
            error: out error,
            join: static (ref SpellingCursor next) => next.TryTake(c: '|'),
            node: out expression,
            readItem: TryReadBoth
        );
    private static bool TryReadBoth(ref SpellingCursor cursor, out CellSetExpression? expression, out string error) =>
        StateSpelling.TryReadInfixList<CellSetExpression>(
            combine: static items => new CellSetExpression.Both(Items: items),
            cursor: ref cursor,
            error: out error,
            join: static (ref SpellingCursor next) => next.TryTake(c: '&'),
            node: out expression,
            readItem: TryReadUnary
        );
    private static bool TryReadUnary(ref SpellingCursor cursor, out CellSetExpression? expression, out string error) {
        if (!cursor.TryTake(c: '~')) {
            return TryReadAtom(
                cursor: ref cursor,
                error: out error,
                expression: out expression
            );
        }
        if (!TryReadUnary(
            cursor: ref cursor,
            error: out error,
            expression: out var item
        )) {
            expression = null;

            return false;
        }

        expression = new CellSetExpression.Complement(Item: item!);

        return true;
    }
    private static bool TryReadAtom(ref SpellingCursor cursor, out CellSetExpression? expression, out string error) {
        expression = null;
        error = string.Empty;

        if (cursor.TryTake(c: '(')) {
            if (!TryReadAny(
                cursor: ref cursor,
                error: out error,
                expression: out expression
            )) {
                return false;
            }
            if (!cursor.TryTake(c: ')')) {
                error = "expects ')' closing a parenthesized cell set";
                expression = null;

                return false;
            }

            return true;
        }
        if (cursor.TryTakeWord(word: "all")) {
            expression = new CellSetExpression.Everything();

            return true;
        }
        if (cursor.TryTakeWord(word: "none")) {
            expression = new CellSetExpression.Nothing();

            return true;
        }

        var kind = (cursor.TryTakeWord(word: "board")
            ? "board"
            : (cursor.TryTakeWord(word: "family")
                ? "family"
                : (cursor.TryTakeWord(word: "zone")
                    ? "zone"
                    : null
                )
            )
        );

        if (kind is null) {
            error = $"expects 'all', 'none', 'board', 'family', or 'zone' at '{cursor.Rest}'";

            return false;
        }
        if (!cursor.TryTake(c: '(')) {
            error = $"expects '(' after '{kind}'";

            return false;
        }
        if (!cursor.TryReadName(
            domain: NameDomain,
            error: out error,
            name: out var name
        )) {
            return false;
        }
        if (!cursor.TryTake(c: ',')) {
            error = $"expects ',' before the value range of '{kind}'";

            return false;
        }
        if (!cursor.TryReadWholeNumber(
            signed: true,
            value: out var low
        )) {
            error = $"expects a whole value bound at '{cursor.Rest}'";

            return false;
        }
        if (
            !cursor.TryTake(c: '.') ||
            !cursor.TryTakeImmediate(c: '.')
        ) {
            error = $"expects '..' between the bounds of '{kind}'";

            return false;
        }
        if (!cursor.TryReadWholeNumber(
            signed: true,
            value: out var high
        )) {
            error = $"expects a whole value bound at '{cursor.Rest}'";

            return false;
        }
        if (!cursor.TryTake(c: ')')) {
            error = $"expects ')' closing '{kind}'";

            return false;
        }
        if (high < low) {
            error = $"reads {low}..{high}, which is not least first";

            return false;
        }
        if (!CellName.TryParse(
            candidate: name,
            name: out var row,
            reason: out var reason
        )) {
            error = $"names '{name}', which {reason}";

            return false;
        }

        expression = (kind switch {
            "board" => new CellSetExpression.Board(
                High: high,
                Low: low,
                Row: row
            ),
            "family" => new CellSetExpression.Family(
                High: high,
                Low: low,
                Name: row
            ),
            _ => new CellSetExpression.Zone(
                High: high,
                Low: low,
                Row: row
            ),
        });

        return true;
    }
}
