using System.Globalization;
using System.Text;

namespace Puck.State;

/// <summary>
/// The infix spelling of a <see cref="ValueExpression"/> — <c>"min(damage, hp) * 2 - armor[$each]"</c> — and
/// its inverse. It is SYNTAX ONLY: a spelling parses to exactly the postfix <see cref="ValueToken"/> list an
/// author could have written by hand. Both spellings share the compiler's validation, constant folding, pricing,
/// and evaluator, so the infix form adds no semantics, no cost, and no second evaluator. Every token kind has one spelling:
/// <list type="bullet">
/// <item><c>+ - * / %</c>, <c>&amp; | ^ ~</c>, <c>&lt;&lt; &gt;&gt; &gt;&gt;&gt;</c>, <c>== != &lt; &lt;= &gt; &gt;=</c>,
/// unary <c>-</c>, and <c>condition ? whenTrue : whenFalse</c> (<c>select</c>), with C precedence.</item>
/// <item>Named forms as calls: <c>min(a, b)</c>, <c>max</c>, <c>clamp(value, min, max)</c>, <c>abs</c>, <c>sign</c>,
/// <c>popCount</c>, <c>leadingZeroCount</c>, <c>trailingZeroCount</c>, <c>lowestSetBit</c>,
/// <c>clearLowestSetBit</c>, <c>byteSwap</c>, <c>bitReverse</c>, <c>rotateLeft(value, count)</c>,
/// <c>replicationMask(width)</c>, <c>repeatBits(pattern, width)</c>,
/// <c>rotateRight</c>, <c>parallelBitExtract(value, mask)</c>, <c>parallelBitDeposit</c>,
/// <c>bitField(value, offset, width)</c>, <c>bitInsert(value, field, offset, width)</c>,
/// <c>boardShift(mask, topology, direction)</c>, <c>boardImage(mask, topology, element)</c>, and
/// <c>select(condition, whenTrue, whenFalse)</c>.</item>
/// <item>A state read is its row name, keyed as <c>row[key]</c>. A bare name starts with a letter, <c>_</c>, or
/// <c>$</c> and continues with letters, digits, <c>_</c>, <c>$</c>, and <c>.</c>; a reserved channel (one starting
/// with <c>$</c>) may also carry <c>:</c> between name characters and a signed segment after a colon, so
/// <c>$table:armor:$each</c> and <c>$board:mask:board:-6:-6</c> are one name each. Any
/// other name — one carrying a hyphen or a space — is spelled between backquotes: <c>`seat-1`</c>. A key is a bare
/// name, a number, or a backquoted name.</item>
/// <item>A table read indexes with the same brackets: <c>$table:moves:power[$bind:move]</c> is the
/// <c>$table:moves:power:$bind:move</c> channel, and prints that way. A key indexed once more —
/// <c>buffs[minion[$each]]</c> — is the <c>$cell:minion:$each</c> indirection: the key read live from that cell.</item>
/// <item>A literal is a decimal (<c>12</c>, <c>0.25</c>) or a hexadecimal mask (<c>0xFF00</c>); a leading minus folds
/// into the literal, so <c>-1</c> is one constant token.</item>
/// </list>
/// The ternary colon must not be glued to a <c>$</c>-name on both sides (<c>a ? $bind:x : 0</c> spaces it), which is
/// the one place the two uses of <c>:</c> could meet.
/// </summary>
public static class ExpressionSpelling {
    /// <summary>The longest spelling admitted, a capacity bound on the parser's input rather than on the expression
    /// (the 64-token ceiling still applies to what it parses to).</summary>
    public const int MaxLength = 4096;

    private static IReadOnlyDictionary<string, ExpressionOperator> Calls => ExpressionOperators.Calls;

    /// <summary>Parses an infix spelling to its postfix token list.</summary>
    /// <param name="text">The spelling.</param>
    /// <param name="tokens">The tokens, in evaluation order, when the spelling parses.</param>
    /// <param name="error">Why it did not, naming the character position, or empty.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a well-formed expression.</returns>
    public static bool TryParse(string? text, out IReadOnlyList<ValueToken> tokens, out string error) {
        tokens = [];
        if (string.IsNullOrWhiteSpace(value: text)) {
            error = "is empty";
            return false;
        }
        if (text.Length > MaxLength) {
            error = $"is {text.Length} characters long; at most {MaxLength} are admitted";
            return false;
        }
        var parser = new Parser(text: text);
        try {
            var root = parser.ParseExpression();
            parser.ExpectEnd();
            var list = new List<ValueToken>();
            root.Emit(into: list);
            tokens = list;
            error = string.Empty;
            return true;
        } catch (SyntaxException failure) {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>Renders a postfix token list in the infix spelling <see cref="TryParse"/> reads back to the same
    /// tokens, with only the parentheses precedence requires.</summary>
    /// <param name="tokens">The postfix tokens.</param>
    /// <param name="text">The spelling, when the list is a well-formed postfix program.</param>
    /// <returns><see langword="false"/> when the list underflows or leaves more than one value — a list the compiler
    /// would refuse too.</returns>
    public static bool TryPrint(IReadOnlyList<ValueToken> tokens, out string text) {
        ArgumentNullException.ThrowIfNull(tokens);
        var stack = new Stack<Node>();
        foreach (var token in tokens) {
            var node = Lower(token: token, stack: stack);
            if (node is null) {
                text = string.Empty;
                return false;
            }
            stack.Push(item: node);
        }
        if (stack.Count != 1) {
            text = string.Empty;
            return false;
        }
        var builder = new StringBuilder();
        stack.Pop().Print(into: builder, parentLevel: 0, rightOperand: false);
        text = builder.ToString();
        return true;
    }

    /// <summary>Renders a postfix token list in the infix spelling, throwing when the list is not a well-formed
    /// postfix program.</summary>
    /// <param name="tokens">The postfix tokens.</param>
    /// <returns>The spelling.</returns>
    /// <exception cref="ArgumentException">The list underflows or leaves more than one value.</exception>
    public static string Print(IReadOnlyList<ValueToken> tokens) =>
        (TryPrint(tokens: tokens, text: out var text)
            ? text
            : throw new ArgumentException(message: "the token list is not a well-formed postfix expression", paramName: nameof(tokens))
        );

    /// <summary>Parses the text between a name's brackets as a cell key, on the same terms as a key inside
    /// <see cref="TryParse"/>: <c>row[key]</c> spells a <c>$cell:</c> indirection, a bare name or reserved token
    /// (<c>$each</c>, <c>$bind:&lt;name&gt;</c>, <c>$cell:&lt;row&gt;:&lt;key&gt;</c>) passes through, and any other
    /// expression becomes an <c>$expr:</c> key.</summary>
    /// <param name="text">The bracket contents.</param>
    /// <param name="key">The key spelling, when the text parses.</param>
    /// <param name="error">Why it did not, naming the character position, or empty.</param>
    public static bool TryParseKey(string? text, out string key, out string error) {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(value: text)) {
            error = "is empty";
            return false;
        }
        if (text.Length > MaxLength) {
            error = $"is {text.Length} characters long; at most {MaxLength} are admitted";
            return false;
        }
        var parser = new Parser(text: text);
        try {
            key = parser.ParseKey();
            parser.ExpectEnd();
            error = string.Empty;
            return true;
        } catch (SyntaxException failure) {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>Whether a name prints bare, without backquotes.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the name lexes as one bare identifier.</returns>
    public static bool IsBareName(string name) =>
        (ScanBareName(text: name, start: 0) == name.Length) && (name.Length > 0) && !Calls.ContainsKey(key: name);

    /// <summary>Scans one bare name out of <paramref name="text"/> starting at <paramref name="start"/>, on exactly
    /// the terms this lexer's own names obey: a letter/<c>_</c>/<c>$</c> start, letter/digit/<c>_</c>/<c>$</c>/<c>.</c>
    /// continuations, and — once the name opens with <c>$</c> — <c>:segment</c> continuations, signed <c>:-N</c>
    /// offsets, and a <see cref="RuleFacts.LiveZonePrefix"/> group folded whole into the name. This is the one
    /// implementation of that walk: <see cref="IsBareName"/> asks it whether a whole string is one name, and callers
    /// outside this assembly (the <c>.puck</c> parser and formatter) scan with it rather than carrying a copy.</summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="start">The offset to scan from.</param>
    /// <returns>The name's length in characters, or 0 when <paramref name="start"/> does not open a name.</returns>
    public static int ScanBareName(string text, int start) {
        ArgumentNullException.ThrowIfNull(argument: text);

        if ((start < 0) || (start >= text.Length) || !IsNameStart(character: text[start])) {
            return 0;
        }
        var reserved = (text[start] == '$');
        var index = start + 1;

        while (index < text.Length) {
            var character = text[index];
            if (IsNamePart(character: character)) {
                index++;
                continue;
            }
            if (reserved && (character == ':') && ((index + 1) < text.Length) && (IsNamePart(character: text[index + 1]) || IsSignedSegment(text: text, index: index + 1))) {
                index++;
                continue;
            }
            if (reserved && (character == '-') && (text[index - 1] == ':') && ((index + 1) < text.Length) && char.IsAsciiDigit(c: text[index + 1])) {
                index++;
                continue;
            }
            if (reserved && (character == '[')) {
                var close = LiveZoneIndexEnd(text: text, start: start, bracket: index);
                if (close > 0) {
                    index = close + 1;
                    continue;
                }
            }
            break;
        }

        return index - start;
    }

    // A "$zones[" segment inside a reserved name carries its whole bracketed index — colons, nested brackets and all —
    // so "$match:run:$zones[game[from]]:prefix" lexes as one name. Answers the index of the closing bracket, or -1
    // when the bracket at `bracket` does not open a live-zone index or never closes.
    private static int LiveZoneIndexEnd(string text, int start, int bracket) {
        var prefix = RuleFacts.LiveZonePrefix.Length - 1;
        if ((text[bracket] != '[') || ((bracket - start) < prefix) || (string.CompareOrdinal(strA: text, indexA: (bracket - prefix), strB: RuleFacts.LiveZonePrefix, indexB: 0, length: prefix) != 0)) {
            return -1;
        }
        if (((bracket - prefix) != start) && (text[bracket - prefix - 1] != ':')) {
            return -1;
        }
        var depth = 0;
        for (var index = bracket; index < text.Length; index++) {
            if (text[index] == '[') {
                depth++;
            } else if ((text[index] == ']') && (--depth == 0)) {
                return index;
            }
        }
        return -1;
    }

    // ":-6" inside a reserved name is a signed offset segment, never a subtraction: a name cannot end in a colon.
    private static bool IsSignedSegment(string text, int index) =>
        (index + 1 < text.Length && text[index] == '-' && char.IsAsciiDigit(c: text[index + 1]));
    private static bool IsNameStart(char character) => (char.IsLetter(c: character) || character == '_' || character == '$');
    private static bool IsNamePart(char character) => (char.IsLetterOrDigit(c: character) || character == '_' || character == '$' || character == '.');

    private static string QuoteName(string name) =>
        (IsBareName(name: name) ? name : $"`{name}`");

    private static Node? Lower(ValueToken token, Stack<Node> stack) {
        switch (token) {
            case ValueToken.Constant constant:
                return new Literal(Value: constant.Value);
            case ValueToken.State state:
                return new StateRead(Name: state.Name, Key: state.Key);
            case ValueToken.BoardShift shift:
                return (stack.Count >= 1) ? new Call(Name: "boardShift", Arguments: [stack.Pop()], Names: [shift.Topology, shift.Direction]) : null;
            case ValueToken.BoardFill fill:
                return (stack.Count >= 1) ? new Call(Name: "boardFill", Arguments: [stack.Pop()], Names: [fill.Topology, fill.Direction]) : null;
            case ValueToken.BoardImage image:
                return (stack.Count >= 1) ? new Call(Name: "boardImage", Arguments: [stack.Pop()], Names: [image.Topology, image.Element]) : null;
            case ValueToken.Select:
                return Pop(stack, 3) is { } branches ? new Ternary(Condition: branches[0], WhenTrue: branches[1], WhenFalse: branches[2]) : null;
            case ValueToken.Negate:
                return (stack.Count >= 1) ? new Unary(Operator: "-", Operand: stack.Pop()) : null;
            case ValueToken.BitNot:
                return (stack.Count >= 1) ? new Unary(Operator: "~", Operand: stack.Pop()) : null;
        }
        if (BinaryOperator(token: token) is { } symbol) {
            return Pop(stack, 2) is { } operands ? new Binary(Operator: symbol, Left: operands[0], Right: operands[1]) : null;
        }
        if (ExpressionOperators.Find(token) is { Name: { } name } descriptor) {
            return Pop(stack, descriptor.Arity) is { } arguments ? new Call(Name: name, Arguments: arguments, Names: []) : null;
        }
        return null;
    }

    private static Node[]? Pop(Stack<Node> stack, int count) {
        if (stack.Count < count) {
            return null;
        }
        var result = new Node[count];
        for (var index = count - 1; index >= 0; index--) {
            result[index] = stack.Pop();
        }
        return result;
    }

    private static string? BinaryOperator(ValueToken token) => ExpressionOperators.Find(token)?.Symbol;
    private static ValueToken BinaryToken(string symbol) => ExpressionOperators.Binary(symbol);

    // Binding strength, C's order: the ternary is loosest, a primary tightest.
    private static int Level(string symbol) => symbol switch {
        "|" => 2,
        "^" => 3,
        "&" => 4,
        "==" or "!=" => 5,
        "<" or "<=" or ">" or ">=" => 6,
        "<<" or ">>" or ">>>" => 7,
        "+" or "-" => 8,
        _ => 9,
    };
    private const int TernaryLevel = 1;
    private const int UnaryLevel = 10;
    private const int PrimaryLevel = 11;

    private abstract record Node {
        public abstract int Level { get; }
        public abstract void Emit(List<ValueToken> into);
        public abstract void PrintBare(StringBuilder into);
        public void Print(StringBuilder into, int parentLevel, bool rightOperand) {
            var parenthesize = ((Level < parentLevel) || (rightOperand && (Level == parentLevel)));
            if (parenthesize) {
                into.Append(value: '(');
            }
            PrintBare(into: into);
            if (parenthesize) {
                into.Append(value: ')');
            }
        }
    }
    private sealed record Literal(decimal Value) : Node {
        public override int Level => PrimaryLevel;
        public override void Emit(List<ValueToken> into) => into.Add(item: new ValueToken.Constant(Value: Value));
        public override void PrintBare(StringBuilder into) => into.Append(value: Value.ToString(provider: CultureInfo.InvariantCulture));
    }
    private sealed record StateRead(string Name, string? Key) : Node {
        public override int Level => PrimaryLevel;
        public override void Emit(List<ValueToken> into) => into.Add(item: new ValueToken.State(Name: Name, Key: Key));
        public override void PrintBare(StringBuilder into) {
            var (name, key) = ((Key is null) && TrySplitTableKey(Name, out var table, out var tableKey))
                ? (table, tableKey)
                : (Name, Key);
            into.Append(value: QuoteName(name: name));
            if (key is { } spelled) {
                into.Append(value: '[');
                AppendKey(into: into, key: spelled);
                into.Append(value: ']');
            }
        }
        // A "$cell:row:key" key prints as row[key], nesting as deep as the indirection goes; an "$expr:" key prints
        // its canonical infix text, which is what the parser produced it from.
        private static void AppendKey(StringBuilder into, string key) {
            if (key.StartsWith(value: RuleFacts.ExpressionKeyPrefix, comparisonType: StringComparison.Ordinal)) {
                // A bare name inside [ ] is a literal key, so an expression that is one bare read prints parenthesized.
                var text = key[RuleFacts.ExpressionKeyPrefix.Length..];
                into.Append(value: (IsBareName(name: text) ? $"({text})" : text));
                return;
            }
            if (key.StartsWith(value: RuleFacts.CellKeyPrefix, comparisonType: StringComparison.Ordinal)) {
                var rest = key[RuleFacts.CellKeyPrefix.Length..];
                var colon = rest.IndexOf(value: ':');
                if (colon > 0 && colon < rest.Length - 1) {
                    into.Append(value: QuoteName(name: rest[..colon])).Append(value: '[');
                    AppendKey(into: into, key: rest[(colon + 1)..]);
                    into.Append(value: ']');
                    return;
                }
            }
            into.Append(value: (IsBareName(name: key) || IsNumberLexeme(key)) ? key : $"`{key}`");
        }
    }
    private sealed record Unary(string Operator, Node Operand) : Node {
        public override int Level => UnaryLevel;
        public override void Emit(List<ValueToken> into) {
            Operand.Emit(into: into);
            into.Add(item: (Operator == "-") ? new ValueToken.Negate() : new ValueToken.BitNot());
        }
        public override void PrintBare(StringBuilder into) {
            into.Append(value: Operator);
            // A unary or negative-literal operand is parenthesized so "- -a" never prints as "--a".
            var wrap = (Operand is Unary || (Operand is Literal { Value: < 0m }));
            if (wrap) { into.Append(value: '('); }
            Operand.Print(into: into, parentLevel: UnaryLevel, rightOperand: false);
            if (wrap) { into.Append(value: ')'); }
        }
    }
    private sealed record Binary(string Operator, Node Left, Node Right) : Node {
        public override int Level => ExpressionSpelling.Level(symbol: Operator);
        public override void Emit(List<ValueToken> into) {
            Left.Emit(into: into);
            Right.Emit(into: into);
            into.Add(item: BinaryToken(symbol: Operator));
        }
        public override void PrintBare(StringBuilder into) {
            Left.Print(into: into, parentLevel: Level, rightOperand: false);
            into.Append(value: ' ').Append(value: Operator).Append(value: ' ');
            Right.Print(into: into, parentLevel: Level, rightOperand: true);
        }
    }
    private sealed record Ternary(Node Condition, Node WhenTrue, Node WhenFalse) : Node {
        public override int Level => TernaryLevel;
        public override void Emit(List<ValueToken> into) {
            Condition.Emit(into: into);
            WhenTrue.Emit(into: into);
            WhenFalse.Emit(into: into);
            into.Add(item: new ValueToken.Select());
        }
        public override void PrintBare(StringBuilder into) {
            Condition.Print(into: into, parentLevel: TernaryLevel + 1, rightOperand: false);
            into.Append(value: " ? ");
            WhenTrue.Print(into: into, parentLevel: TernaryLevel + 1, rightOperand: false);
            into.Append(value: " : ");
            WhenFalse.Print(into: into, parentLevel: TernaryLevel, rightOperand: false);
        }
    }
    private sealed record Call(string Name, Node[] Arguments, string[] Names) : Node {
        public override int Level => PrimaryLevel;
        public override void Emit(List<ValueToken> into) {
            foreach (var argument in Arguments) {
                argument.Emit(into: into);
            }
            into.Add(item: Name switch {
                "boardShift" => new ValueToken.BoardShift(Topology: Names[0], Direction: Names[1]),
                "boardFill" => new ValueToken.BoardFill(Topology: Names[0], Direction: Names[1]),
                "boardImage" => new ValueToken.BoardImage(Topology: Names[0], Element: Names[1]),
                _ => Calls[Name].Make(),
            });
        }
        public override void PrintBare(StringBuilder into) {
            into.Append(value: Name).Append(value: '(');
            for (var index = 0; index < Arguments.Length; index++) {
                if (index > 0) { into.Append(value: ", "); }
                Arguments[index].Print(into: into, parentLevel: 0, rightOperand: false);
            }
            foreach (var name in Names) {
                into.Append(value: ", ").Append(value: QuoteName(name: name));
            }
            into.Append(value: ')');
        }
    }

    // "$table:t[:column]:<key>" splits before its key: a "$"-spelled key ("$bind:x", "$cell:r:k", "$each") at the
    // last ":$", else the last colon.
    private static bool TrySplitTableKey(string name, out string table, out string key) {
        table = name;
        key = string.Empty;
        if (!name.StartsWith(value: RuleFacts.TablePrefix, comparisonType: StringComparison.Ordinal)) {
            return false;
        }
        var split = name.LastIndexOf(value: ":$", comparisonType: StringComparison.Ordinal);
        if (split < RuleFacts.TablePrefix.Length) {
            split = name.LastIndexOf(value: ':');
        }
        if (split < RuleFacts.TablePrefix.Length || split == name.Length - 1) {
            return false;
        }
        table = name[..split];
        key = name[(split + 1)..];
        return true;
    }

    private static bool IsNumberLexeme(string text) {
        if (text.Length == 0 || !char.IsAsciiDigit(c: text[0])) {
            return false;
        }
        foreach (var character in text) {
            if (!char.IsAsciiDigit(c: character) && character != '.') {
                return false;
            }
        }
        return true;
    }

    private sealed class SyntaxException(string message) : Exception(message: message);

    private enum Lexeme : byte { End, Number, Name, Punctuation }

    // A recursive-descent parser over a one-token lookahead lexer; the grammar is small enough that the two live in
    // one class and the token stream is never materialized.
    private sealed class Parser(string text) {
        private int m_position;
        private Lexeme m_kind;
        private string m_value = string.Empty;
        private bool m_quoted;
        private int m_start;
        private bool m_primed;

        private void Prime() {
            if (!m_primed) {
                Advance();
                m_primed = true;
            }
        }
        private SyntaxException Fail(string message) => new(message: $"at character {m_start + 1}: {message}");

        private void Advance() {
            while (m_position < text.Length && char.IsWhiteSpace(c: text[m_position])) {
                m_position++;
            }
            m_start = m_position;
            m_quoted = false;
            if (m_position >= text.Length) {
                m_kind = Lexeme.End;
                m_value = string.Empty;
                return;
            }
            var character = text[m_position];
            if (char.IsAsciiDigit(c: character)) {
                var end = m_position;
                if (character == '0' && end + 1 < text.Length && (text[end + 1] == 'x' || text[end + 1] == 'X')) {
                    end += 2;
                    while (end < text.Length && char.IsAsciiHexDigit(c: text[end])) { end++; }
                } else {
                    while (end < text.Length && (char.IsAsciiDigit(c: text[end]) || text[end] == '.')) { end++; }
                }
                m_kind = Lexeme.Number;
                m_value = text[m_position..end];
                m_position = end;
                return;
            }
            if (character == '`') {
                var close = text.IndexOf(value: '`', startIndex: m_position + 1);
                if (close < 0) {
                    throw Fail(message: "a backquoted name is not closed");
                }
                m_kind = Lexeme.Name;
                m_value = text[(m_position + 1)..close];
                m_quoted = true;
                m_position = close + 1;
                if (m_value.Length == 0) {
                    throw Fail(message: "a backquoted name is empty");
                }
                return;
            }
            if (IsNameStart(character: character)) {
                var reserved = (character == '$');
                var end = m_position + 1;
                while (end < text.Length) {
                    if (IsNamePart(character: text[end])) {
                        end++;
                        continue;
                    }
                    if (reserved && text[end] == ':' && end + 1 < text.Length && IsNamePart(character: text[end + 1])) {
                        end++;
                        continue;
                    }
                    if (reserved && text[end] == ':' && IsSignedSegment(text, end + 1)) {
                        end += 2;
                        continue;
                    }
                    if (reserved && LiveZoneIndexEnd(text: text, start: m_position, bracket: end) is var close && close > 0) {
                        end = close + 1;
                        continue;
                    }
                    break;
                }
                m_kind = Lexeme.Name;
                m_value = text[m_position..end];
                m_position = end;
                return;
            }
            foreach (var punctuation in Punctuations) {
                if (string.CompareOrdinal(strA: text, indexA: m_position, strB: punctuation, indexB: 0, length: punctuation.Length) == 0) {
                    m_kind = Lexeme.Punctuation;
                    m_value = punctuation;
                    m_position += punctuation.Length;
                    return;
                }
            }
            throw Fail(message: $"unexpected character '{character}'");
        }
        // Longest first, so ">>>" wins over ">>" over ">".
        private static readonly string[] Punctuations = [">>>", "<<", ">>", "==", "!=", "<=", ">=", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^", "~", "?", ":", "(", ")", "[", "]", ","];

        private bool Accept(string punctuation) {
            Prime();
            if (m_kind == Lexeme.Punctuation && m_value == punctuation) {
                Advance();
                return true;
            }
            return false;
        }
        private void Expect(string punctuation) {
            if (!Accept(punctuation: punctuation)) {
                throw Fail(message: $"expected '{punctuation}'{Found()}");
            }
        }
        private string Found() {
            Prime();
            return m_kind switch {
                Lexeme.End => " but reached the end",
                _ => $" but found '{m_value}'",
            };
        }
        public void ExpectEnd() {
            Prime();
            if (m_kind != Lexeme.End) {
                throw Fail(message: $"unexpected '{m_value}' after the expression");
            }
        }

        public Node ParseExpression() => ParseTernary();

        private Node ParseTernary() {
            var condition = ParseBinary(minimumLevel: 2);
            if (!Accept(punctuation: "?")) {
                return condition;
            }
            var whenTrue = ParseTernary();
            Expect(punctuation: ":");
            var whenFalse = ParseTernary();
            return new Ternary(Condition: condition, WhenTrue: whenTrue, WhenFalse: whenFalse);
        }
        // Precedence climbing over the binary table: every operator is left-associative.
        private Node ParseBinary(int minimumLevel) {
            var left = ParseUnary();
            while (true) {
                Prime();
                if (m_kind != Lexeme.Punctuation || BinaryOperator(symbol: m_value) is not { } symbol) {
                    return left;
                }
                var level = Level(symbol: symbol);
                if (level < minimumLevel) {
                    return left;
                }
                Advance();
                var right = ParseBinary(minimumLevel: level + 1);
                left = new Binary(Operator: symbol, Left: left, Right: right);
            }
        }
        private static string? BinaryOperator(string symbol) => symbol switch {
            "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "<<" or ">>" or ">>>" or "==" or "!=" or "<" or "<=" or ">" or ">=" => symbol,
            _ => null,
        };
        private Node ParseUnary() {
            if (Accept(punctuation: "-")) {
                var operand = ParseUnary();
                return (operand is Literal literal)
                    ? new Literal(Value: -literal.Value)
                    : new Unary(Operator: "-", Operand: operand);
            }
            if (Accept(punctuation: "~")) {
                return new Unary(Operator: "~", Operand: ParseUnary());
            }
            return ParsePrimary();
        }
        private Node ParsePrimary() {
            Prime();
            switch (m_kind) {
                case Lexeme.Number: {
                        var lexeme = m_value;
                        var start = m_start;
                        Advance();
                        return new Literal(Value: ParseNumber(lexeme: lexeme, start: start));
                    }
                case Lexeme.Name: {
                        var name = m_value;
                        var quoted = m_quoted;
                        Advance();
                        if (!quoted && Calls.TryGetValue(key: name, value: out var call)) {
                            return ParseCall(name: name, arity: call.Arity, names: 0);
                        }
                        if (!quoted && (name is "boardShift" or "boardFill" or "boardImage")) {
                            return ParseCall(name: name, arity: 1, names: 2);
                        }
                        if (!quoted && Accept(punctuation: "(")) {
                            throw Fail(message: $"'{name}' is not a function; a state read is a bare name (or `{name}` to read a row of that name)");
                        }
                        string? key = null;
                        if (Accept(punctuation: "[")) {
                            key = ParseKey();
                            Expect(punctuation: "]");
                        }
                        if ((key is not null) && !quoted && name.StartsWith(value: RuleFacts.TablePrefix, comparisonType: StringComparison.Ordinal)) {
                            return new StateRead(Name: $"{name}:{key}", Key: null);
                        }
                        return new StateRead(Name: name, Key: key);
                    }
                case Lexeme.Punctuation when m_value == "(": {
                        Advance();
                        var inner = ParseExpression();
                        Expect(punctuation: ")");
                        return inner;
                    }
                case Lexeme.End:
                    throw Fail(message: "expected a value but reached the end");
                default:
                    throw Fail(message: $"expected a value but found '{m_value}'");
            }
        }
        // A key: a bare or backquoted name, a number, a name indexed once more — row[key], the "$cell:row:key"
        // indirection read live from another cell — or any other expression, which becomes an "$expr:" key the
        // compiler turns into an implicit binding. The simple forms are recognised by lookahead and the lexer rewound
        // when the key turns out to be an expression after all (row[other[k] + 1]).
        public string ParseKey() {
            Prime();
            var saved = (m_position, m_kind, m_value, m_quoted, m_start, m_primed);
            if (m_kind is Lexeme.Name or Lexeme.Number) {
                var key = m_value;
                var indexable = ((m_kind == Lexeme.Name) && !m_quoted);
                Advance();
                Prime();
                // The key ends at the closing bracket — or at the end of the text, when the key is parsed alone.
                if (KeyEnds()) {
                    return key;
                }
                if (indexable && Accept(punctuation: "[")) {
                    var inner = ParseKey();
                    Expect(punctuation: "]");
                    Prime();
                    if (KeyEnds() && !inner.StartsWith(value: RuleFacts.ExpressionKeyPrefix, comparisonType: StringComparison.Ordinal)) {
                        return $"{RuleFacts.CellKeyPrefix}{key}:{inner}";
                    }
                }
                (m_position, m_kind, m_value, m_quoted, m_start, m_primed) = saved;
            }
            var node = ParseExpression();
            var tokens = new List<ValueToken>();
            node.Emit(into: tokens);
            return $"{RuleFacts.ExpressionKeyPrefix}{Print(tokens: tokens)}";
        }
        private bool KeyEnds() => ((m_kind == Lexeme.End) || ((m_kind == Lexeme.Punctuation) && (m_value == "]")));
        private Node ParseCall(string name, int arity, int names) {
            Expect(punctuation: "(");
            var arguments = new Node[arity];
            for (var index = 0; index < arity; index++) {
                if (index > 0) { Expect(punctuation: ","); }
                arguments[index] = ParseExpression();
            }
            var extra = new string[names];
            for (var index = 0; index < names; index++) {
                Expect(punctuation: ",");
                Prime();
                if (m_kind != Lexeme.Name) {
                    throw Fail(message: $"'{name}' takes a name here{Found()}");
                }
                extra[index] = m_value;
                Advance();
            }
            if (Accept(punctuation: ",")) {
                throw Fail(message: $"'{name}' takes {arity + names} argument(s)");
            }
            Expect(punctuation: ")");
            return new Call(Name: name, Arguments: arguments, Names: extra);
        }
        private decimal ParseNumber(string lexeme, int start) {
            if (lexeme.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if (lexeme.Length == 2 || !ulong.TryParse(s: lexeme.AsSpan(start: 2), style: NumberStyles.AllowHexSpecifier, provider: CultureInfo.InvariantCulture, result: out var bits)) {
                    throw new SyntaxException(message: $"at character {start + 1}: '{lexeme}' is not a hexadecimal literal of at most 16 digits");
                }
                return bits;
            }
            if (!decimal.TryParse(s: lexeme, style: NumberStyles.AllowDecimalPoint, provider: CultureInfo.InvariantCulture, result: out var value)) {
                throw new SyntaxException(message: $"at character {start + 1}: '{lexeme}' is not a number");
            }
            return value;
        }
    }
}
