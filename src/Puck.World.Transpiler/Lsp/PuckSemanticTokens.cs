using System.Collections.Frozen;
using Puck.State;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Units;

namespace Puck.World.Transpiler.Lsp;

/// <summary>The role a highlighted span of <c>.puck</c> source plays. Each member's value is its index in
/// <see cref="PuckSemanticTokens.TokenTypes"/>, the legend the language server advertises.</summary>
public enum PuckSemanticRole {
    /// <summary>A member name: a scalar before its <c>:</c>, a block or array member before its <c>{</c>/<c>[</c>, a
    /// named argument, or a member read after a <c>.</c>.</summary>
    Property = 0,
    /// <summary>A name that stands for a value: a <c>let</c>, a loop or lambda variable, a row, a backquoted
    /// name.</summary>
    Variable = 1,
    /// <summary>A word the grammar reads as structure, an interpolation hole's braces, a string escape, or a unit
    /// suffix.</summary>
    Keyword = 2,
    /// <summary>A called name, or a declared template or module.</summary>
    Function = 3,
    /// <summary>A capitalized name a declaration keyword introduces, such as a shape's primitive.</summary>
    Type = 4,
    /// <summary>A string's text, including the literal runs of an interpolated string.</summary>
    String = 5,
    /// <summary>A numeric literal or a colour literal.</summary>
    Number = 6,
    /// <summary>A capitalized bare name, or <c>true</c>, <c>false</c> or <c>null</c>.</summary>
    EnumMember = 7,
    /// <summary>An operator or a parenthesis.</summary>
    Operator = 8,
    /// <summary>A line or block comment.</summary>
    Comment = 9,
}
/// <summary>The modifiers a highlighted span can carry; each flag's bit position is its index in
/// <see cref="PuckSemanticTokens.TokenModifiers"/>.</summary>
[Flags]
public enum PuckSemanticModifiers {
    /// <summary>No modifier.</summary>
    None = 0,
    /// <summary>The span declares the name it spells: a <c>let</c>, a loop variable, a template, a module, or a
    /// composition's world.</summary>
    Declaration = 1,
}
/// <summary>One highlighted span of <c>.puck</c> source.</summary>
/// <param name="Offset">The span's 0-based character offset.</param>
/// <param name="Length">The span's length in characters; a string or comment may cross lines.</param>
/// <param name="Role">What the span is.</param>
/// <param name="Modifiers">What else is true of it.</param>
public readonly record struct PuckSemanticToken(int Offset, int Length, PuckSemanticRole Role, PuckSemanticModifiers Modifiers = PuckSemanticModifiers.None);
/// <summary>Classifies <c>.puck</c> source for highlighting: the language server's
/// <c>textDocument/semanticTokens/full</c> answer, which VS Code and World Studio both color from.</summary>
/// <remarks>
/// <para>Lexical boundaries are the parser's own: a string, raw string, backquoted name or comment ends where
/// <see cref="SourceLexemes.End"/> says it does, an identifier is <see cref="IdentifierSpelling"/>'s, an
/// interpolation hole closes as the parser's hole scan closes it (strings inside the hole skipped, brackets
/// counted), and a unit suffix is one <see cref="UnitConversion"/> accepts. The roles follow the grammar's
/// positions: a statement's first word followed by a name or string is a construct keyword (<c>rule "x"</c>,
/// <c>shape Prism</c>), followed by <c>{</c> or <c>[</c> after a space it is a member (<c>noise {</c>), and
/// followed by <c>:</c> anywhere it is a member (<c>exponent: 2</c>, <c>f(name: x)</c>), except the row a
/// <c>slot</c>, <c>table</c> or <c>grid</c> declares, whose <c>: Enum</c> leaves it the name it is without one. An
/// operator is one token however many characters spell it, the infix ones read from
/// <see cref="ExpressionOperators.Infix"/>. The roles are the ones the
/// VS Code extension's TextMate grammar distinguishes (<c>editors/vscode/README.md</c>'s syntax-colour table).</para>
/// <para>Brackets, braces, commas, semicolons and member dots carry no token, so an editor's bracket colouring stays
/// in charge of them. The body of an embedded-language block (<c>sql { … }</c>) carries none either, so the editor's
/// grammar for that language colours it.</para>
/// <para>A classification never fails: text that does not parse is still classified, word by word, since an
/// editor highlights what the author is typing.</para>
/// </remarks>
public static class PuckSemanticTokens {
    /// <summary>Gets the token type legend, indexed by <see cref="PuckSemanticRole"/>. Every name is one of the Language
    /// Server Protocol's standard semantic token types.</summary>
    public static IReadOnlyList<string> TokenTypes { get; } = ["property", "variable", "keyword", "function", "type", "string", "number", "enumMember", "operator", "comment"];
    /// <summary>Gets the token modifier legend, indexed by the bit position of each <see cref="PuckSemanticModifiers"/>
    /// flag.</summary>
    public static IReadOnlyList<string> TokenModifiers { get; } = ["declaration"];

    // Words the grammar reads as structure wherever they stand, including where a statement-head word would read as a
    // member (`else {`, `given {`) and between operands (`and`, `in`).
    private static readonly FrozenSet<string> Reserved = FrozenSet.ToFrozenSet(
        comparer: StringComparer.Ordinal,
        source: [
            "and", "as", "break", "each", "else", "entry", "expect", "export", "for", "from", "given", "if", "import",
            "in", "let", "module", "not", "of", "or", "refused", "repeat", "template", "test", "ticks", "to", "until",
            "use", "when", "with",
        ]
    );
    // The keywords whose next bare word is the name they declare.
    private static readonly FrozenSet<string> DeclaresValue = FrozenSet.ToFrozenSet(
        comparer: StringComparer.Ordinal,
        source: ["for", "let", "world"]
    );
    private static readonly FrozenSet<string> DeclaresFunction = FrozenSet.ToFrozenSet(
        comparer: StringComparer.Ordinal,
        source: ["module", "template"]
    );
    // The keywords whose declared name may be followed by a `: Enum` annotation, where a name followed by `:` is the row
    // it declares rather than a member.
    private static readonly FrozenSet<string> AnnotatesName = FrozenSet.ToFrozenSet(
        comparer: StringComparer.Ordinal,
        source: ["grid", "slot", "table"]
    );
    private static readonly FrozenSet<string> Units = FrozenSet.ToFrozenSet(
        comparer: StringComparer.Ordinal,
        source: Enum.GetValues<UnitDimension>().SelectMany(selector: static dimension => UnitConversion.AcceptedUnits(dimension: dimension))
    );
    // Every operator spelled with more than one character, longest first, so `>>>` is never read as `>>` or `>`: the
    // infix operators are the operator table's own, and the rest are the grammar's arrow, range and compound
    // assignments.
    private static readonly string[] LongOperators = [.. ExpressionOperators.Infix
        .Select(selector: static row => row.Symbol!)
        .Concat(second: ["=>", "..", "+=", "-=", "*=", "/=", "%=", "&&", "||"])
        .Where(predicate: static symbol => (symbol.Length > 1))
        .Distinct(comparer: StringComparer.Ordinal)
        .OrderByDescending(keySelector: static symbol => symbol.Length)];

    /// <summary>Classifies <paramref name="source"/>.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="vocabulary">The vocabulary the source is written in, which names its embedded-language blocks;
    /// none when omitted.</param>
    /// <returns>Every token, in source order; no two overlap, and each lies inside <paramref name="source"/>.</returns>
    public static IReadOnlyList<PuckSemanticToken> Classify(string source, IDocumentVocabulary? vocabulary = null) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var tokens = new List<PuckSemanticToken>();

        new Classifier(
            source: source,
            tokens: tokens,
            vocabulary: vocabulary
        ).Run(
            end: source.Length,
            expression: false,
            start: 0
        );

        return tokens;
    }
    /// <summary>Encodes <paramref name="tokens"/> as a <c>SemanticTokens.data</c> array: five integers per token —
    /// line delta, start-character delta, length, type index, modifier bits — with a token that crosses a line break
    /// split into one token per line, since a client need not support multi-line tokens.</summary>
    /// <param name="source">The source the tokens were classified from.</param>
    /// <param name="tokens">The tokens, in source order.</param>
    /// <returns>The encoded data.</returns>
    public static IReadOnlyList<int> Encode(string source, IReadOnlyList<PuckSemanticToken> tokens) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var data = new List<int>();
        var line = 0;
        var lineStart = 0;
        var scanned = 0;
        var previousLine = 0;
        var previousCharacter = 0;

        foreach (var token in tokens) {
            var start = token.Offset;
            var end = (token.Offset + token.Length);

            while (start < end) {
                // Advance the line counter to the line holding `start`.
                for (; (scanned < start); ++scanned) {
                    if (source[scanned] == '\n') {
                        ++line;
                        lineStart = (scanned + 1);
                    }
                }

                var lineEnd = source.IndexOf(
                    startIndex: start,
                    value: '\n'
                );
                var pieceEnd = (((lineEnd < 0) || (lineEnd >= end))
                    ? end
                    : lineEnd
                );
                // A carriage return belongs to the line break, never to a token.
                var visibleEnd = (((pieceEnd > start) && (pieceEnd == lineEnd) && (source[(pieceEnd - 1)] == '\r'))
                    ? (pieceEnd - 1)
                    : pieceEnd
                );

                if (visibleEnd > start) {
                    var character = (start - lineStart);

                    data.Add(item: (line - previousLine));
                    data.Add(item: ((line == previousLine)
                        ? (character - previousCharacter)
                        : character
                    ));
                    data.Add(item: (visibleEnd - start));
                    data.Add(item: ((int)token.Role));
                    data.Add(item: ((int)token.Modifiers));
                    previousLine = line;
                    previousCharacter = character;
                }

                start = ((pieceEnd == end)
                    ? end
                    : (pieceEnd + 1)
                );
            }
        }

        return data;
    }

    private sealed class Classifier(string source, List<PuckSemanticToken> tokens, IDocumentVocabulary? vocabulary) {
        // The innermost open bracket: '{' (or none) where statements stand, '[' or '(' inside an expression.
        private readonly Stack<char> m_open = new();

        // Whether the next word opens a statement.
        private bool m_head;
        // The keyword the previous token was, or null.
        private string? m_previousKeyword;

        private void Emit(int offset, int length, PuckSemanticRole role, PuckSemanticModifiers modifiers = PuckSemanticModifiers.None) {
            if (length > 0) {
                tokens.Add(item: new PuckSemanticToken(
                    Length: length,
                    Modifiers: modifiers,
                    Offset: offset,
                    Role: role
                ));
            }
        }

        private bool InStatements => ((m_open.Count == 0) || (m_open.Peek() == '{'));

        public void Run(int start, int end, bool expression) {
            m_head = !expression;

            if (expression) {
                m_open.Push(item: '(');
            }

            var index = start;

            while (index < end) {
                var c = source[index];

                if (c == '\n') {
                    if (InStatements) {
                        m_head = true;
                    }
                    ++index;
                    continue;
                }
                if (char.IsWhiteSpace(c: c)) {
                    ++index;
                    continue;
                }

                var lexicalEnd = Math.Min(
                    val1: end,
                    val2: SourceLexemes.End(
                        offset: index,
                        source: source
                    )
                );

                if (lexicalEnd > index) {
                    index = Lexeme(
                        end: lexicalEnd,
                        start: index
                    );
                    continue;
                }
                if (char.IsAsciiDigit(c: c)) {
                    index = Number(
                        end: end,
                        start: index
                    );
                    Settle();
                    continue;
                }
                if (IdentifierSpelling.IsStart(character: c) || ((c == IdentifierSpelling.Sigil) && ((index + 1) < end) && IdentifierSpelling.IsStart(character: source[(index + 1)]))) {
                    index = Word(
                        end: end,
                        start: index
                    );
                    continue;
                }
                if ((c == '#') && TryColor(
                    end: end,
                    length: out var colorLength,
                    start: index
                )) {
                    Emit(
                        length: colorLength,
                        offset: index,
                        role: PuckSemanticRole.Number
                    );
                    index += colorLength;
                    Settle();
                    continue;
                }

                index = Punctuation(
                    end: end,
                    start: index
                );
            }
        }

        // A word position's role is settled; the next word no longer opens a statement nor follows a keyword.
        private void Settle() {
            m_head = false;
            m_previousKeyword = null;
        }
        private int Lexeme(int start, int end) {
            var c = source[start];

            if (c == '/') {
                Emit(
                    length: (end - start),
                    offset: start,
                    role: PuckSemanticRole.Comment
                );
                // A comment is trivia: it neither opens nor closes a statement.
                return end;
            }
            if (c == '`') {
                var property = ((NextSignificant(from: end) == ':') && !IsDoubleColon(at: NextSignificantIndex(from: end)));

                Emit(
                    length: (end - start),
                    offset: start,
                    role: (property
                        ? PuckSemanticRole.Property
                        : PuckSemanticRole.Variable
                    )
                );
                Settle();
                return end;
            }

            StringLiteral(
                end: end,
                start: start
            );
            Settle();
            return end;
        }
        private void StringLiteral(int start, int end) {
            var interpolated = (source[start] == '$');
            var quote = (interpolated
                ? (start + 1)
                : start
            );
            var raw = source.AsSpan(start: quote).StartsWith(value: "\"\"\"");
            var fence = (raw
                ? 3
                : 1
            );
            var bodyStart = Math.Min(
                val1: end,
                val2: (quote + fence)
            );
            var bodyEnd = Math.Max(
                val1: bodyStart,
                val2: ((((end - bodyStart) >= fence) && source.AsSpan(length: fence, start: (end - fence)).SequenceEqual(other: (raw ? "\"\"\"" : "\"")))
                    ? (end - fence)
                    : end)
            );
            var run = start;

            for (var index = bodyStart; (index < bodyEnd);) {
                var c = source[index];

                if (!raw && (c == '\\') && ((index + 1) < bodyEnd)) {
                    Emit(
                        length: (index - run),
                        offset: run,
                        role: PuckSemanticRole.String
                    );
                    Emit(
                        length: 2,
                        offset: index,
                        role: PuckSemanticRole.Keyword
                    );
                    index += 2;
                    run = index;
                    continue;
                }
                if (interpolated && (c is '{' or '}') && ((index + 1) < bodyEnd) && (source[(index + 1)] == c)) {
                    Emit(
                        length: (index - run),
                        offset: run,
                        role: PuckSemanticRole.String
                    );
                    Emit(
                        length: 2,
                        offset: index,
                        role: PuckSemanticRole.Keyword
                    );
                    index += 2;
                    run = index;
                    continue;
                }
                if (interpolated && (c == '{')) {
                    var close = HoleEnd(
                        end: bodyEnd,
                        start: (index + 1)
                    );

                    Emit(
                        length: (index - run),
                        offset: run,
                        role: PuckSemanticRole.String
                    );
                    Emit(
                        length: 1,
                        offset: index,
                        role: PuckSemanticRole.Keyword
                    );
                    new Classifier(
                        source: source,
                        tokens: tokens,
                        vocabulary: vocabulary
                    ).Run(
                        end: ((close < 0)
                            ? bodyEnd
                            : close
                        ),
                        expression: true,
                        start: (index + 1)
                    );

                    if (close < 0) {
                        run = bodyEnd;
                        index = bodyEnd;
                        break;
                    }

                    Emit(
                        length: 1,
                        offset: close,
                        role: PuckSemanticRole.Keyword
                    );
                    index = (close + 1);
                    run = index;
                    continue;
                }

                ++index;
            }

            Emit(
                length: (end - run),
                offset: run,
                role: PuckSemanticRole.String
            );
        }
        // The hole's closing brace, counting nested brackets and skipping any string the expression carries, as the
        // parser's own hole scan does; -1 when the string ends first.
        private int HoleEnd(int start, int end) {
            var depth = 0;

            for (var index = start; (index < end); ++index) {
                var c = source[index];

                if (c == '"') {
                    var close = SourceLexemes.End(
                        offset: index,
                        source: source
                    );

                    index = (Math.Min(
                        val1: end,
                        val2: close
                    ) - 1);
                    continue;
                }
                if (c is '{' or '[' or '(') {
                    depth++;
                } else if (c is ']' or ')') {
                    depth--;
                } else if (c == '}') {
                    if (depth == 0) {
                        return index;
                    }
                    depth--;
                }
            }

            return -1;
        }
        private int Number(int start, int end) {
            var index = start;

            if ((source[index] == '0') && ((index + 1) < end) && (source[(index + 1)] is 'x' or 'X')) {
                index += 2;
                while ((index < end) && (char.IsAsciiHexDigit(c: source[index]) || (source[index] == '_'))) {
                    ++index;
                }
            } else {
                index = Digits(
                    end: end,
                    start: index
                );

                // A '.' followed by a digit continues the number; '..' is the range operator.
                if (((index + 1) < end) && (source[index] == '.') && char.IsAsciiDigit(c: source[(index + 1)])) {
                    index = Digits(
                        end: end,
                        start: (index + 1)
                    );
                }
                if (((index + 1) < end) && (source[index] is 'e' or 'E')) {
                    var exponent = (index + 1);

                    if ((exponent < end) && (source[exponent] is '+' or '-')) {
                        ++exponent;
                    }
                    if ((exponent < end) && char.IsAsciiDigit(c: source[exponent])) {
                        index = Digits(
                            end: end,
                            start: exponent
                        );
                    }
                }
            }

            Emit(
                length: (index - start),
                offset: start,
                role: PuckSemanticRole.Number
            );

            // A unit suffix written against the number.
            var unitLength = (((index < end) && (source[index] == '%'))
                ? 1
                : IdentifierSpelling.ScanIdentifier(
                    start: index,
                    text: source.AsSpan(length: end, start: 0)
                ));

            if ((unitLength > 0) && Units.Contains(item: source.Substring(length: unitLength, startIndex: index))) {
                Emit(
                    length: unitLength,
                    offset: index,
                    role: PuckSemanticRole.Keyword
                );
                index += unitLength;
            }

            return index;
        }
        private int Digits(int start, int end) {
            var index = start;

            while ((index < end) && (char.IsAsciiDigit(c: source[index]) || (source[index] == '_'))) {
                ++index;
            }

            return index;
        }
        private bool TryColor(int start, int end, out int length) {
            var index = (start + 1);

            while ((index < end) && char.IsAsciiHexDigit(c: source[index])) {
                ++index;
            }

            length = (index - start);

            return (
                ((length - 1) is 3 or 4 or 6 or 8) &&
                ((index >= end) || !IdentifierSpelling.IsPart(character: source[index]))
            );
        }
        private int Word(int start, int end) {
            var length = IdentifierSpelling.ScanName(
                start: start,
                text: source.AsSpan(length: end, start: 0)
            );
            var wordEnd = (start + length);
            var word = source.Substring(length: length, startIndex: start);
            var next = NextSignificantIndex(from: wordEnd);
            var following = ((next < source.Length)
                ? source[next]
                : '\0'
            );
            var head = m_head;
            var previousKeyword = m_previousKeyword;
            var member = ((start > 0) && (source[(start - 1)] == '.') && ((start < 2) || (source[(start - 2)] != '.')));
            PuckSemanticRole role;
            var modifiers = PuckSemanticModifiers.None;
            string? keyword = null;

            if (word is "true" or "false" or "null") {
                role = PuckSemanticRole.EnumMember;
            } else if ((previousKeyword is not null) && DeclaresFunction.Contains(item: previousKeyword)) {
                role = PuckSemanticRole.Function;
                modifiers = PuckSemanticModifiers.Declaration;
            } else if ((previousKeyword is not null) && DeclaresValue.Contains(item: previousKeyword) && !Reserved.Contains(item: word)) {
                role = PuckSemanticRole.Variable;
                modifiers = PuckSemanticModifiers.Declaration;
            } else if ((following == ':') && !IsDoubleColon(at: next) && !((previousKeyword is not null) && AnnotatesName.Contains(item: previousKeyword))) {
                role = PuckSemanticRole.Property;
            } else if (Reserved.Contains(item: word)) {
                role = PuckSemanticRole.Keyword;
                keyword = word;
            } else if (following == '(') {
                role = PuckSemanticRole.Function;
            } else if (member) {
                role = PuckSemanticRole.Property;
            } else if ((previousKeyword is "as" or "shape") && char.IsAsciiLetterUpper(c: word[0])) {
                role = PuckSemanticRole.Type;
            } else if (head && (following is '{' or '[') && (next > wordEnd)) {
                if (IsEmbeddedLanguage(word: word) && (following == '{')) {
                    Emit(
                        length: length,
                        offset: start,
                        role: PuckSemanticRole.Keyword
                    );
                    Settle();
                    return SkipBlock(open: next);
                }
                role = PuckSemanticRole.Property;
            } else if ((head || (previousKeyword is "entry" or "export")) && OpensName(at: next)) {
                if (IsEmbeddedLanguage(word: word)) {
                    Emit(
                        length: length,
                        offset: start,
                        role: PuckSemanticRole.Keyword
                    );
                    Settle();
                    return EmbeddedBlock(after: next);
                }
                role = PuckSemanticRole.Keyword;
                keyword = word;
            } else if (char.IsAsciiLetterUpper(c: word[0])) {
                role = PuckSemanticRole.EnumMember;
            } else {
                role = PuckSemanticRole.Variable;
            }

            Emit(
                length: length,
                modifiers: modifiers,
                offset: start,
                role: role
            );
            Settle();
            m_previousKeyword = keyword;

            return wordEnd;
        }
        private bool IsEmbeddedLanguage(string word) => ((vocabulary is not null) && vocabulary.IsEmbeddedLanguage(identifier: word));
        // `sql "name" { … }`: the name is a string, and the body belongs to the embedded language.
        private int EmbeddedBlock(int after) {
            var index = after;

            if (source[index] is '"' or '$') {
                var close = SourceLexemes.End(
                    offset: index,
                    source: source
                );

                if (close > index) {
                    Emit(
                        length: (close - index),
                        offset: index,
                        role: PuckSemanticRole.String
                    );
                    index = NextSignificantIndex(from: close);
                }
            }

            return (((index < source.Length) && (source[index] == '{'))
                ? SkipBlock(open: index)
                : index
            );
        }
        // Past the brace that closes the one at `open`, leaving everything inside it unclassified.
        private int SkipBlock(int open) {
            var depth = 0;

            for (var index = open; (index < source.Length); ++index) {
                if (source[index] == '{') {
                    ++depth;
                } else if ((source[index] == '}') && (--depth == 0)) {
                    return (index + 1);
                }
            }

            return source.Length;
        }
        private bool OpensName(int at) {
            if (at >= source.Length) {
                return false;
            }

            var c = source[at];

            return (
                IdentifierSpelling.IsStart(character: c) ||
                (c is '"' or '`') ||
                ((c == IdentifierSpelling.Sigil) && ((at + 1) < source.Length) && ((source[(at + 1)] == '"') || IdentifierSpelling.IsStart(character: source[(at + 1)])))
            );
        }
        private bool IsDoubleColon(int at) => (((at + 1) < source.Length) && (source[(at + 1)] == ':'));
        private char NextSignificant(int from) {
            var index = NextSignificantIndex(from: from);

            return ((index < source.Length)
                ? source[index]
                : '\0'
            );
        }
        // The next character after spaces and tabs on the same line.
        private int NextSignificantIndex(int from) {
            var index = from;

            while ((index < source.Length) && (source[index] is ' ' or '\t')) {
                ++index;
            }

            return index;
        }
        private int Punctuation(int start, int end) {
            var c = source[start];

            switch (c) {
                case '{':
                    m_open.Push(item: '{');
                    Settle();
                    m_head = true;
                    return (start + 1);
                case '[':
                    m_open.Push(item: '[');
                    Settle();
                    return (start + 1);
                case '(':
                    m_open.Push(item: '(');
                    Emit(
                        length: 1,
                        offset: start,
                        role: PuckSemanticRole.Operator
                    );
                    Settle();
                    return (start + 1);
                case '}' or ']' or ')':
                    if (m_open.Count > 0) {
                        _ = m_open.Pop();
                    }
                    if (c == ')') {
                        Emit(
                            length: 1,
                            offset: start,
                            role: PuckSemanticRole.Operator
                        );
                    }
                    Settle();
                    m_head = ((c == '}') && InStatements);
                    return (start + 1);
                case ';':
                    Settle();
                    m_head = InStatements;
                    return (start + 1);
                case ',':
                    Settle();
                    return (start + 1);
                case '.' when (((start + 1) >= end) || (source[(start + 1)] != '.')):
                    Settle();
                    return (start + 1);
            }

            var length = 0;

            foreach (var candidate in LongOperators) {
                if (((start + candidate.Length) <= end) && source.AsSpan(start: start, length: candidate.Length).SequenceEqual(other: candidate)) {
                    length = candidate.Length;
                    break;
                }
            }

            if ((length == 0) && (c is '+' or '-' or '*' or '/' or '%' or '=' or '<' or '>' or '!' or ':' or '&' or '|' or '?' or '^' or '~')) {
                length = 1;
            }

            // Anything the grammar does not know is left to the editor.
            if (length > 0) {
                Emit(
                    length: length,
                    offset: start,
                    role: PuckSemanticRole.Operator
                );
            }

            Settle();
            return (start + Math.Max(
                val1: 1,
                val2: length
            ));
        }
    }
}
