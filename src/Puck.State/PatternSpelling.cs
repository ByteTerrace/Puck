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
    private const int RepetitionLevel = 5;
    private const int SequenceLevel = 3;

    private static readonly string[] ReservedWords = ["any", "empty", "except", "none"];

    private static int LevelOf(PatternNode node) => node switch {
        PatternNode.Choice => ChoiceLevel,
        PatternNode.Both => BothLevel,
        PatternNode.Sequence => SequenceLevel,
        PatternNode.Complement => ComplementLevel,
        PatternNode.Optional or PatternNode.Star or PatternNode.Plus or PatternNode.Repeat => RepetitionLevel,
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

        return !ReservedWords.Contains(value: name);
    }
    private static string SymbolText(string name) => (IsBareIdentifier(name: name)
        ? name
        : $"\"{name.Replace(
            newValue: "\\\\",
            oldValue: "\\"
        ).Replace(
            newValue: "\\\"",
            oldValue: "\""
        )}\""
    );
    private static bool TryPrintInto(PatternNode? node, int minimumLevel, StringBuilder text) {
        if (node is null) {
            return false;
        }

        var parenthesize = (LevelOf(node: node) < minimumLevel);

        if (parenthesize) {
            _ = text.Append(value: '(');
        }
        if (!TryPrintBody(
            node: node,
            text: text
        )) {
            return false;
        }
        if (parenthesize) {
            _ = text.Append(value: ')');
        }

        return true;
    }
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
                    return TryPrintList(
                        items: sequence.Items,
                        minimumLevel: ComplementLevel,
                        separator: " ",
                        text: text
                    );
                }
            case PatternNode.Choice choice: {
                    return TryPrintList(
                        items: choice.Items,
                        minimumLevel: BothLevel,
                        separator: " | ",
                        text: text
                    );
                }
            case PatternNode.Both both: {
                    return TryPrintList(
                        items: both.Items,
                        minimumLevel: SequenceLevel,
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
    private static bool TryPrintList(IReadOnlyList<PatternNode>? items, string separator, int minimumLevel, StringBuilder text) {
        if (items is not { Count: > 1 }) {
            return false;
        }

        for (var index = 0; (index < items.Count); index++) {
            if (index > 0) {
                _ = text.Append(value: separator);
            }
            if (!TryPrintInto(
                minimumLevel: minimumLevel,
                node: items[index],
                text: text
            )) {
                return false;
            }
        }

        return true;
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
    public static bool TryPrint(PatternNode? node, out string text) {
        var builder = new StringBuilder();

        if (!TryPrintInto(
            minimumLevel: ChoiceLevel,
            node: node,
            text: builder
        )) {
            text = string.Empty;

            return false;
        }

        text = builder.ToString();

        return true;
    }
    /// <summary>Parses pattern text into a node.</summary>
    /// <param name="text">The text.</param>
    /// <param name="node">The node, on success.</param>
    /// <param name="error">Why the text was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the text parsed.</returns>
    public static bool TryParse(string? text, out PatternNode? node, out string error) {
        node = null;

        if (string.IsNullOrWhiteSpace(value: text)) {
            error = "is empty; a pattern names at least one symbol";

            return false;
        }

        var reader = new Reader(text: text);

        if (!reader.TryReadChoice(
            error: out error,
            node: out node
        )) {
            return false;
        }

        reader.SkipSpace();

        if (!reader.AtEnd) {
            error = $"carries '{text[reader.Offset..]}' after the pattern";
            node = null;

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
        public bool TryReadChoice(out PatternNode? node, out string error) {
            if (!TryReadBoth(
                error: out error,
                node: out node
            )) {
                return false;
            }

            var items = new List<PatternNode> { node! };

            while (TryTake(c: '|')) {
                if (!TryReadBoth(
                    error: out error,
                    node: out var item
                )) {
                    node = null;

                    return false;
                }
                items.Add(item: item!);
            }

            node = ((items.Count == 1)
                ? items[0]
                : new PatternNode.Choice(Items: items)
            );

            return true;
        }

        private bool TryReadBoth(out PatternNode? node, out string error) {
            if (!TryReadSequence(
                error: out error,
                node: out node
            )) {
                return false;
            }

            var items = new List<PatternNode> { node! };

            while (TryTake(c: '&')) {
                if (!TryReadSequence(
                    error: out error,
                    node: out var item
                )) {
                    node = null;

                    return false;
                }
                items.Add(item: item!);
            }

            node = ((items.Count == 1)
                ? items[0]
                : new PatternNode.Both(Items: items)
            );

            return true;
        }
        private bool TryReadSequence(out PatternNode? node, out string error) {
            if (!TryReadUnary(
                error: out error,
                node: out node
            )) {
                return false;
            }

            var items = new List<PatternNode> { node! };

            while (StartsAtom()) {
                if (!TryReadUnary(
                    error: out error,
                    node: out var item
                )) {
                    node = null;

                    return false;
                }
                items.Add(item: item!);
            }

            node = ((items.Count == 1)
                ? items[0]
                : new PatternNode.Sequence(Items: items)
            );

            return true;
        }
        private bool TryReadUnary(out PatternNode? node, out string error) {
            if (TryTake(c: '~')) {
                if (!TryReadUnary(
                    error: out error,
                    node: out var item
                )) {
                    node = null;

                    return false;
                }

                node = new PatternNode.Complement(Item: item!);

                return true;
            }

            return TryReadRepetition(
                error: out error,
                node: out node
            );
        }
        private bool TryReadRepetition(out PatternNode? node, out string error) {
            if (!TryReadAtom(
                error: out error,
                node: out node
            )) {
                return false;
            }

            while (true) {
                SkipSpace();
                if (TryTake(c: '*')) {
                    node = new PatternNode.Star(Item: node!);

                    continue;
                }
                if (TryTake(c: '+')) {
                    node = new PatternNode.Plus(Item: node!);

                    continue;
                }
                if (TryTake(c: '?')) {
                    node = new PatternNode.Optional(Item: node!);

                    continue;
                }
                if (!TryTake(c: '{')) {
                    return true;
                }
                if (!TryReadCount(
                    count: out var minimum,
                    error: out error
                )) {
                    node = null;

                    return false;
                }

                var maximum = minimum;

                if (TryTake(c: ',') && !TryReadCount(
                    count: out maximum,
                    error: out error
                )) {
                    node = null;

                    return false;
                }
                if (!TryTake(c: '}')) {
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
        private bool TryReadAtom(out PatternNode? node, out string error) {
            SkipSpace();

            node = null;
            error = string.Empty;

            if (TryTake(c: '(')) {
                if (!TryReadChoice(
                    error: out error,
                    node: out node
                )) {
                    return false;
                }
                if (!TryTake(c: ')')) {
                    error = "expects ')' closing a parenthesized pattern";
                    node = null;

                    return false;
                }

                return true;
            }
            if (TryTakeWord(word: "any")) {
                node = new PatternNode.AnySymbol();

                return true;
            }
            if (TryTakeWord(word: "empty")) {
                node = new PatternNode.Nothing();

                return true;
            }
            if (TryTakeWord(word: "none")) {
                node = new PatternNode.None();

                return true;
            }
            if (TryTakeWord(word: "except")) {
                if (!TryTake(c: '(')) {
                    error = "expects '(' after 'except'";

                    return false;
                }
                if (!TryReadSymbolName(
                    error: out error,
                    name: out var excluded
                )) {
                    return false;
                }
                if (!TryTake(c: ')')) {
                    error = "expects ')' closing 'except'";

                    return false;
                }

                node = new PatternNode.Except(Name: excluded);

                return true;
            }
            if (!TryReadSymbolName(
                error: out error,
                name: out var symbol
            )) {
                return false;
            }

            node = new PatternNode.Symbol(Name: symbol);

            return true;
        }
        private bool TryReadCount(out int count, out string error) {
            SkipSpace();

            var start = m_offset;

            while (
                (m_offset < m_text.Length) &&
                char.IsAsciiDigit(c: m_text[m_offset])
            ) {
                m_offset++;
            }

            if (
                (m_offset == start) ||
                !int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out count,
                s: m_text.AsSpan(length: (m_offset - start), start: start)
            )
            ) {
                count = 0;
                error = "expects a whole repetition count";

                return false;
            }

            error = string.Empty;

            return true;
        }
        private bool TryReadSymbolName(out string name, out string error) {
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
                    error = "expects '\"' closing a symbol name";

                    return false;
                }

                name = quoted.ToString();

                if (name.Length == 0) {
                    error = "expects a symbol name between the quotes";

                    return false;
                }

                return true;
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
                error = $"expects a symbol name at '{m_text[m_offset..]}'";

                return false;
            }

            name = m_text[start..m_offset];

            return true;
        }
        private bool StartsAtom() {
            SkipSpace();

            if (m_offset >= m_text.Length) {
                return false;
            }

            var c = m_text[m_offset];

            return (
                (c is '(' or '~' or '"') ||
                char.IsAsciiLetter(c: c) ||
                (c == '_')
            );
        }
        private bool TryTake(char c) {
            SkipSpace();
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
