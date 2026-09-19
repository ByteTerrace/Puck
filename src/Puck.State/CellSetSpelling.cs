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

    private static int LevelOf(CellSetExpression expression) => expression switch {
        CellSetExpression.Any => AnyLevel,
        CellSetExpression.Both => BothLevel,
        CellSetExpression.Complement => ComplementLevel,
        _ => LeafLevel,
    };
    private static bool IsBareIdentifier(string name) {
        if (
            (name.Length == 0) ||
            (!char.IsAsciiLetter(c: name[0]) && (name[0] != '_'))
        ) {
            return false;
        }

        foreach (var c in name) {
            if (
                !char.IsAsciiLetterOrDigit(c: c) &&
                (c != '_')
            ) {
                return false;
            }
        }

        return true;
    }
    private static string NameText(CellName name) => (IsBareIdentifier(name: name.Value)
        ? name.Value
        : $"\"{name.Value.Replace(
            newValue: "\\\"",
            oldValue: "\""
        )}\""
    );
    private static string SourceText(string kind, CellName name, long low, long high) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{kind}({NameText(name: name)}, {low}..{high})"
    );
    private static bool TryPrintInto(CellSetExpression? expression, int minimumLevel, StringBuilder text) {
        if (expression is null) {
            return false;
        }

        var parenthesize = (LevelOf(expression: expression) < minimumLevel);

        if (parenthesize) {
            _ = text.Append(value: '(');
        }
        if (!TryPrintBody(
            expression: expression,
            text: text
        )) {
            return false;
        }
        if (parenthesize) {
            _ = text.Append(value: ')');
        }

        return true;
    }
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
                    return TryPrintList(
                        items: any.Items,
                        minimumLevel: BothLevel,
                        separator: " | ",
                        text: text
                    );
                }
            case CellSetExpression.Both both: {
                    return TryPrintList(
                        items: both.Items,
                        minimumLevel: ComplementLevel,
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
    private static bool TryPrintList(IReadOnlyList<CellSetExpression>? items, string separator, int minimumLevel, StringBuilder text) {
        if (items is not { Count: > 1 }) {
            return false;
        }

        for (var index = 0; (index < items.Count); index++) {
            if (index > 0) {
                _ = text.Append(value: separator);
            }
            if (!TryPrintInto(
                expression: items[index],
                minimumLevel: minimumLevel,
                text: text
            )) {
                return false;
            }
        }

        return true;
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
    public static bool TryPrint(CellSetExpression? expression, out string text) {
        var builder = new StringBuilder();

        if (!TryPrintInto(
            expression: expression,
            minimumLevel: AnyLevel,
            text: builder
        )) {
            text = string.Empty;

            return false;
        }

        text = builder.ToString();

        return true;
    }
    /// <summary>Parses cell-set text into an expression.</summary>
    /// <param name="text">The text.</param>
    /// <param name="expression">The expression, on success.</param>
    /// <param name="error">Why the text was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the text parsed.</returns>
    public static bool TryParse(string? text, out CellSetExpression? expression, out string error) {
        expression = null;

        if (string.IsNullOrWhiteSpace(value: text)) {
            error = "is empty; a cell set names a board, family, or zone";

            return false;
        }

        var reader = new Reader(text: text);

        if (!reader.TryReadAny(
            error: out error,
            expression: out expression
        )) {
            return false;
        }

        reader.SkipSpace();

        if (!reader.AtEnd) {
            error = $"carries '{text[reader.Offset..]}' after the cell set";
            expression = null;

            return false;
        }

        return true;
    }

    private ref struct Reader(string text) {
        private readonly string m_text = text;
        private int m_offset = 0;

        public readonly bool AtEnd => (m_offset >= m_text.Length);
        public readonly int Offset => m_offset;

        public void SkipSpace() {
            while (
                (m_offset < m_text.Length) &&
                char.IsWhiteSpace(c: m_text[m_offset])
            ) {
                m_offset++;
            }
        }
        public bool TryReadAny(out CellSetExpression? expression, out string error) {
            if (!TryReadBoth(
                error: out error,
                expression: out expression
            )) {
                return false;
            }

            var items = new List<CellSetExpression> { expression! };

            while (TryTake(c: '|')) {
                if (!TryReadBoth(
                    error: out error,
                    expression: out var item
                )) {
                    expression = null;

                    return false;
                }
                items.Add(item: item!);
            }

            expression = ((items.Count == 1)
                ? items[0]
                : new CellSetExpression.Any(Items: items)
            );

            return true;
        }

        private bool TryReadBoth(out CellSetExpression? expression, out string error) {
            if (!TryReadUnary(
                error: out error,
                expression: out expression
            )) {
                return false;
            }

            var items = new List<CellSetExpression> { expression! };

            while (TryTake(c: '&')) {
                if (!TryReadUnary(
                    error: out error,
                    expression: out var item
                )) {
                    expression = null;

                    return false;
                }
                items.Add(item: item!);
            }

            expression = ((items.Count == 1)
                ? items[0]
                : new CellSetExpression.Both(Items: items)
            );

            return true;
        }
        private bool TryReadUnary(out CellSetExpression? expression, out string error) {
            if (TryTake(c: '~')) {
                if (!TryReadUnary(
                    error: out error,
                    expression: out var item
                )) {
                    expression = null;

                    return false;
                }

                expression = new CellSetExpression.Complement(Item: item!);

                return true;
            }

            return TryReadAtom(
                error: out error,
                expression: out expression
            );
        }
        private bool TryReadAtom(out CellSetExpression? expression, out string error) {
            SkipSpace();

            expression = null;
            error = string.Empty;

            if (TryTake(c: '(')) {
                if (!TryReadAny(
                    error: out error,
                    expression: out expression
                )) {
                    return false;
                }
                if (!TryTake(c: ')')) {
                    error = "expects ')' closing a parenthesized cell set";
                    expression = null;

                    return false;
                }

                return true;
            }
            if (TryTakeWord(word: "all")) {
                expression = new CellSetExpression.Everything();

                return true;
            }
            if (TryTakeWord(word: "none")) {
                expression = new CellSetExpression.Nothing();

                return true;
            }

            var kind = (TryTakeWord(word: "board")
                ? "board"
                : (TryTakeWord(word: "family")
                    ? "family"
                    : (TryTakeWord(word: "zone")
                        ? "zone"
                        : null
                    )
                )
            );

            if (kind is null) {
                error = $"expects 'all', 'none', 'board', 'family', or 'zone' at '{m_text[m_offset..]}'";

                return false;
            }
            if (!TryTake(c: '(')) {
                error = $"expects '(' after '{kind}'";

                return false;
            }
            if (!TryReadName(
                error: out error,
                name: out var name
            )) {
                return false;
            }
            if (!TryTake(c: ',')) {
                error = $"expects ',' before the value range of '{kind}'";

                return false;
            }
            if (!TryReadBound(
                bound: out var low,
                error: out error
            )) {
                return false;
            }
            if (
                !TryTake(c: '.') ||
                !TryTakeImmediate(c: '.')
            ) {
                error = $"expects '..' between the bounds of '{kind}'";

                return false;
            }
            if (!TryReadBound(
                bound: out var high,
                error: out error
            )) {
                return false;
            }
            if (!TryTake(c: ')')) {
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
        private bool TryReadBound(out long bound, out string error) {
            SkipSpace();

            var start = m_offset;

            if (TryTakeImmediate(c: '-')) {
                // The sign is part of the literal.
            }
            while (
                (m_offset < m_text.Length) &&
                char.IsAsciiDigit(c: m_text[m_offset])
            ) {
                m_offset++;
            }
            if (!long.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out bound,
                s: m_text.AsSpan(length: (m_offset - start), start: start)
            )) {
                bound = 0;
                error = $"expects a whole value bound at '{m_text[start..]}'";

                return false;
            }

            error = string.Empty;

            return true;
        }
        private bool TryReadName(out string name, out string error) {
            SkipSpace();

            name = string.Empty;
            error = string.Empty;

            if (TryTake(c: '"')) {
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
                if (!TryTake(c: '"')) {
                    error = "expects '\"' closing a row name";

                    return false;
                }

                name = quoted.ToString();

                return (name.Length > 0);
            }

            var start = m_offset;

            if (
                (m_offset < m_text.Length) &&
                (char.IsAsciiLetter(c: m_text[m_offset]) || (m_text[m_offset] == '_'))
            ) {
                m_offset++;
                while (
                    (m_offset < m_text.Length) &&
                    (char.IsAsciiLetterOrDigit(c: m_text[m_offset]) || (m_text[m_offset] == '_'))
                ) {
                    m_offset++;
                }
            }
            if (m_offset == start) {
                error = $"expects a row name at '{m_text[m_offset..]}'";

                return false;
            }

            name = m_text[start..m_offset];

            return true;
        }
        private bool TryTake(char c) {
            SkipSpace();

            return TryTakeImmediate(c: c);
        }
        private bool TryTakeImmediate(char c) {
            if (
                (m_offset >= m_text.Length) ||
                (m_text[m_offset] != c)
            ) {
                return false;
            }
            m_offset++;

            return true;
        }
        private bool TryTakeWord(string word) {
            SkipSpace();

            var end = (m_offset + word.Length);

            if (
                (end > m_text.Length) ||
                !m_text.AsSpan(start: m_offset, length: word.Length).SequenceEqual(other: word)
            ) {
                return false;
            }
            if (
                (end < m_text.Length) &&
                (char.IsAsciiLetterOrDigit(c: m_text[end]) || (m_text[end] == '_'))
            ) {
                return false;
            }
            m_offset = end;

            return true;
        }
    }
}
