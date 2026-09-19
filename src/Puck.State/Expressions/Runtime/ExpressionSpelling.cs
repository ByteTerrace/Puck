using System.Globalization;
using System.Text;

namespace Puck.State;

/// <summary>
/// The infix front end of the expression IR — <c>"min(damage, hp) * 2 - armor[$each]"</c> — and its inverse. It is
/// syntax only: a spelling parses to exactly the <see cref="Instruction"/> list an author could have written by
/// hand, and printing a program yields a spelling that parses back to it. Every instruction has one spelling:
/// <list type="bullet">
/// <item><c>+ - * / %</c>, <c>&amp; | ^ ~</c>, <c>&lt;&lt; &gt;&gt; &gt;&gt;&gt;</c>, <c>== != &lt; &lt;= &gt; &gt;=</c>,
/// unary <c>-</c>, and <c>condition ? whenTrue : whenFalse</c> (<c>select</c>), with C precedence.</item>
/// <item>Named forms as calls: <c>min(a, b)</c>, <c>max</c>, <c>clamp(value, min, max)</c>, <c>abs</c>, <c>sign</c>,
/// <c>popCount</c>, <c>leadingZeroCount</c>, <c>trailingZeroCount</c>, <c>lowestSetBit</c>,
/// <c>clearLowestSetBit</c>, <c>byteSwap</c>, <c>bitReverse</c>, <c>rotateLeft(value, count)</c>,
/// <c>replicationMask(width)</c>, <c>repeatBits(pattern, width)</c>,
/// <c>rotateRight</c>, <c>parallelBitExtract(value, mask)</c>, <c>parallelBitDeposit</c>,
/// <c>bitField(value, offset, width)</c>, <c>bitInsert(value, field, offset, width)</c>,
/// <c>boardShift(mask, topology, direction)</c>, <c>boardImage(mask, topology, element)</c>,
/// <c>select(condition, whenTrue, whenFalse)</c>, and <c>isAbsent(operand)</c>.</item>
/// <item><c>operand ?? fallback</c> is the coalesce operator, binding looser than every other binary operator and
/// tighter than the ternary.</item>
/// <item>A fold over a family is <c>all(family, member -&gt; expr)</c> and its siblings <c>any</c>, <c>count</c>,
/// and <c>sum</c>: the binder names the member the body reads, and the body becomes a subprogram of the program the
/// fold belongs to. A fold nests at most once, so at most one binder is in scope.</item>
/// <item>A state read is its row name, keyed as <c>row[key]</c> or, for a literal key, <c>row.key</c> — one dot,
/// on an unreserved, unquoted name; more than one dot, or a key half that is itself reserved (<c>row.$each</c>), is
/// a parse error naming the fix — a dynamic key admits bracket form only. A bare name starts with a
/// letter, <c>_</c>, or <c>$</c> and continues with letters, digits, <c>_</c>, <c>$</c>, and <c>.</c>; a reserved
/// channel (one starting with <c>$</c>) keeps every dotted segment it carries — the dot-access split never applies
/// there — and may also carry <c>:</c> between name characters and a signed segment after a colon, so
/// <c>$table:armor:$each</c> and <c>$board:mask:board:-6:-6</c> are one name each. Any
/// other name — one carrying a hyphen or a space — is spelled between backquotes: <c>`seat-1`</c>, which likewise
/// never splits at a dot. A key is a bare name, a number, or a backquoted name.</item>
/// <item>A table read indexes with the same brackets: <c>$table:moves:power[$local:move]</c> is the
/// <c>$table:moves:power:$local:move</c> channel, and prints that way. A key indexed once more —
/// <c>buffs[minion[$each]]</c> — is the <c>$cell:minion:$each</c> indirection: the key read live from that cell.</item>
/// <item>A literal is a decimal (<c>12</c>, <c>0.25</c>) or a hexadecimal mask (<c>0xFF00</c>); a leading minus folds
/// into the literal, so <c>-1</c> is one constant token.</item>
/// </list>
/// The ternary colon must not be glued to a <c>$</c>-name on both sides (<c>a ? $local:x : 0</c> spaces it), which is
/// the one place the two uses of <c>:</c> could meet.
/// </summary>
public static class ExpressionSpelling {
    private const int CoalesceLevel = 2;
    // One step tighter than a relational comparison, so an operand whose own operator binds looser prints inside
    // parentheses and the joined `left cmp right` text re-parses as the program it was printed from.
    private const int ComparisonOperandLevel = 8;
    private const int PrimaryLevel = 12;
    private const int TernaryLevel = 1;
    private const int UnaryLevel = 11;

    /// <summary>The longest spelling admitted, in characters: a bound on the parser's input rather than on the
    /// expression, whose token ceiling still applies to what it parses to.</summary>
    public const int MaxLength = 16_384;
    /// <summary>The deepest a spelling may nest: parentheses, brackets and arguments, a chain of prefix operators, a
    /// chain of ternaries, and the left spine a run of binary operators builds all spend it. The parser and every
    /// walk over the tree it builds recurse once per level, so this is what keeps a spelling's depth off the
    /// machine stack; it is as deep as the longest program is long, so it refuses nothing that could compile.</summary>
    public const int MaxNesting = 256;

    private static readonly ExpressionProgram EmptyProgram = new(Instructions: []);

    private static IReadOnlyDictionary<string, ExpressionOperator> Calls => ExpressionOperators.Calls;

    private static string? BinaryOperator(Instruction instruction) => ExpressionOperators.Find(instruction: instruction)?.Symbol;
    private static Instruction BinaryToken(string symbol) => ExpressionOperators.Binary(symbol: symbol);
    private static ExpressionOp FoldOperation(string name) => name switch {
        "all" => ExpressionOp.All,
        "any" => ExpressionOp.Any,
        "count" => ExpressionOp.Count,
        _ => ExpressionOp.Sum,
    };
    private static bool IsFoldName(string name) => (name is "all" or "any" or "count" or "sum");
    private static string? FoldName(ExpressionOp operation) => operation switch {
        ExpressionOp.All => "all",
        ExpressionOp.Any => "any",
        ExpressionOp.Count => "count",
        ExpressionOp.Sum => "sum",
        _ => null,
    };
    private static bool IsNamePart(char character) => (char.IsLetterOrDigit(c: character) || (character == '_') || (character == '$') || (character == '.'));
    private static bool IsNameStart(char character) => (char.IsLetter(c: character) || (character == '_') || (character == '$'));
    private static bool IsNumberLexeme(string text) {
        if (
            (text.Length == 0) ||
            !char.IsAsciiDigit(c: text[0])
        ) {
            return false;
        }
        foreach (var character in text) {
            if (
                !char.IsAsciiDigit(c: character) &&
                (character != '.')
            ) {
                return false;
            }
        }
        return true;
    }
    // ":-6" inside a reserved name is a signed offset segment, never a subtraction: a name cannot end in a colon.
    private static bool IsSignedSegment(string text, int index) =>
        (((index + 1) < text.Length) && (text[index] == '-') && char.IsAsciiDigit(c: text[(index + 1)]));
    // Dot access's one rule: an unreserved name splits at its FIRST dot into a row and a literal key, and admits no
    // second one. The reserved ($) exclusion is the caller's job (ParsePrimary checks it before calling; a public
    // caller rewriting text, not compiling it, does the same) — but that exclusion only covers a name whose ROW half
    // starts with '$' ("$each" itself never reaches here). A key half starting with '$' ("row.$each") is a dynamic
    // key wearing dot syntax, not a literal one, and is refused the same as a trailing or repeated dot: bracket form
    // ("row[$each]") is the only spelling for a dynamic key. False with `error` empty means "no dot to split"; false
    // with `error` set names the fix for a dot the name carries but cannot split on (trailing, more than one, or a
    // reserved key) — the parser turns that into a diagnostic, a text rewriter leaves the name untouched.
    private static bool TrySplitDot(string name, out string row, out string key, out string? error) {
        row = name;
        key = string.Empty;
        error = null;

        var dot = name.IndexOf(value: '.');

        if (dot < 0) {
            return false;
        }
        var rest = name[(dot + 1)..];

        if (rest.Length == 0) {
            error = $"'{name}' ends with a dot; a dotted read is 'row.key' — write the key after the dot";

            return false;
        }
        if (rest.Contains(value: '.')) {
            error = $"'{name}' carries more than one dot; a dotted read admits exactly one — write '{name[..dot]}[{rest}]' to key by the rest";

            return false;
        }
        if (rest.StartsWith(value: '$')) {
            error = $"'{name}' keys by a reserved token, not a literal; a dynamic key needs bracket form — write '{name[..dot]}[{rest}]'";

            return false;
        }
        row = name[..dot];
        key = rest;

        return true;
    }
    // Binding strength, C's order: the ternary is loosest, a primary tightest.
    private static int Level(string symbol) => symbol switch {
        "??" => CoalesceLevel,
        "|" => 3,
        "^" => 4,
        "&" => 5,
        "==" or "!=" => 6,
        "<" or "<=" or ">" or ">=" => 7,
        "<<" or ">>" or ">>>" => 8,
        "+" or "-" => 9,
        _ => 10,
    };
    // A "$zones[" segment inside a reserved name carries its whole bracketed index — colons, nested brackets and all —
    // so "$match:run:$zones[game[from]]:prefix" lexes as one name. Answers the index of the closing bracket, or -1
    // when the bracket at `bracket` does not open a live-zone index or never closes.
    private static int LiveZoneIndexEnd(string text, int start, int bracket) {
        var prefix = (RuleFacts.LiveZonePrefix.Length - 1);

        if (
            (text[bracket] != '[') ||
            ((bracket - start) < prefix) ||
            (string.CompareOrdinal(
            indexA: (bracket - prefix),
            indexB: 0,
            length: prefix,
            strA: text,
            strB: RuleFacts.LiveZonePrefix
        ) != 0)
        ) {
            return -1;
        }
        if (
            ((bracket - prefix) != start) &&
            (text[((bracket - prefix) - 1)] != ':')
        ) {
            return -1;
        }
        var depth = 0;

        for (var index = bracket; (index < text.Length); index++) {
            if (text[index] == '[') {
                depth++;
            } else if (
                (text[index] == ']') &&
                (--depth == 0)
            ) {
                return index;
            }
        }
        return -1;
    }
    private static Node? Lower(Instruction instruction, Stack<Node> stack, IReadOnlyList<Subprogram> subprograms, string? binder) {
        switch (instruction) {
            case { Payload: InstructionPayload.Constant constant }:
                return new Literal(Value: constant.Value);
            case { Payload: InstructionPayload.State state }:
                // Inside a fold a bare name equal to the binder reads the member, so a row of that name keeps its
                // backquotes to stay a row.
                return new StateRead(
                    Key: state.Key,
                    Name: state.Name,
                    Quoted: string.Equals(
                        a: state.Name,
                        b: binder,
                        comparisonType: StringComparison.Ordinal
                    )
                );
            case { Payload: InstructionPayload.Vector vector }: {
                    var opName = instruction.Operation switch {
                        ExpressionOp.Dot => "dot",
                        ExpressionOp.Similarity => "similarity",
                        ExpressionOp.Identical => "identical",
                        _ => null
                    };

                    if (opName is null) { return null; }

                    return new Call(
                        Arguments: [LowerVectorOperand(operand: vector.Left), LowerVectorOperand(operand: vector.Right)],
                        Name: opName,
                        Names: []
                    );
                }
            case { Payload: InstructionPayload.Board board }:
                return (((stack.Count >= 1) && (instruction.Operation switch {
                    ExpressionOp.BoardShift => "boardShift",
                    ExpressionOp.BoardFill => "boardFill",
                    ExpressionOp.BoardImage => "boardImage",
                    _ => null,
                } is { } boardName))
                    ? new Call(
                        Name: boardName,
                        Arguments: [stack.Pop()],
                        Names: [board.Topology, board.Index]
                    )
                    : null
                );
            case { Payload: InstructionPayload.Fold fold }:
                return (((FoldName(operation: instruction.Operation) is { } foldName) &&
                    IsBinderName(name: fold.Binder) &&
                    (((uint)fold.Subprogram) < ((uint)subprograms.Count)) &&
                    (LowerBody(
                    binder: fold.Binder,
                    body: subprograms[fold.Subprogram],
                    subprograms: subprograms
                ) is { } body))
                    ? new FoldCall(
                        Binder: fold.Binder,
                        Body: body,
                        Family: fold.Family,
                        Name: foldName
                    )
                    : null
                );
            case { Operation: ExpressionOp.Member }:
                return ((binder is { } bound)
                    ? new Member(Name: bound)
                    : null
                );
            case { Operation: ExpressionOp.Select }:
                return ((Pop(
                    count: 3,
                    stack: stack
                ) is { } branches)
                    ? new Ternary(
                        Condition: branches[0],
                        WhenTrue: branches[1],
                        WhenFalse: branches[2]
                    )
                    : null
                );
            case { Operation: ExpressionOp.Negate }:
                return ((stack.Count >= 1)
                    ? new Unary(
                        Operator: "-",
                        Operand: stack.Pop()
                    )
                    : null
                );
            case { Operation: ExpressionOp.BitNot }:
                return ((stack.Count >= 1)
                    ? new Unary(
                        Operator: "~",
                        Operand: stack.Pop()
                    )
                    : null
                );
        }
        if (BinaryOperator(instruction: instruction) is { } symbol) {
            return ((Pop(
                count: 2,
                stack: stack
            ) is { } operands)
                ? new Binary(
                    Operator: symbol,
                    Left: operands[0],
                    Right: operands[1]
                )
                : null
            );
        }
        if (ExpressionOperators.Find(instruction: instruction) is { Name: { } name } descriptor) {
            return ((Pop(
                stack,
                descriptor.Arity
            ) is { } arguments)
                ? new Call(
                    Arguments: arguments,
                    Name: name,
                    Names: []
                )
                : null
            );
        }
        return null;
    }
    // A fold body is a whole program of its own, so it lowers through the same walk against a fresh stack.
    private static Node? LowerBody(Subprogram body, string binder, IReadOnlyList<Subprogram> subprograms) {
        var stack = new Stack<Node>();

        foreach (var instruction in body.Instructions) {
            var node = Lower(
                binder: binder,
                instruction: instruction,
                stack: stack,
                subprograms: subprograms
            );

            if (node is null) { return null; }

            stack.Push(item: node);
        }
        return ((stack.Count == 1)
            ? stack.Pop()
            : null
        );
    }
    private static Node[]? Pop(Stack<Node> stack, int count) {
        if (stack.Count < count) {
            return null;
        }
        var result = new Node[count];

        for (var index = (count - 1); (index >= 0); index--) {
            result[index] = stack.Pop();
        }
        return result;
    }
    private static string QuoteName(string name) =>
        (RowNameNeedsBackquoteForDotSafety(name: name)
            ? $"`{name}`"
            : (IsBareName(name: name)
                ? name
                : $"`{name}`"
        ));
    // A row name printed bare re-parses through ParsePrimary's own dot-access split, not through ParseKey (which
    // never splits): an unreserved name carrying a literal dot must print backquoted, or its printed form would
    // re-parse as a dotted read of a different row entirely, rather than the one whole name it started as.
    private static bool RowNameNeedsBackquoteForDotSafety(string name) => (!name.StartsWith(value: '$') && name.Contains(value: '.'));
    // "$table:t[:column]:<key>" splits before its key: a "$"-spelled key ("$local:x", "$cell:r:k", "$each") at the
    // last ":$", else the last colon.
    private static bool TrySplitTableKey(string name, out string table, out string key) {
        table = name;
        key = string.Empty;
        if (!name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.TablePrefix
        )) {
            return false;
        }
        var split = name.LastIndexOf(
            comparisonType: StringComparison.Ordinal,
            value: ":$"
        );

        if (split < RuleFacts.TablePrefix.Length) {
            split = name.LastIndexOf(value: ':');
        }
        if (
            (split < RuleFacts.TablePrefix.Length) ||
            (split == (name.Length - 1))
        ) {
            return false;
        }
        table = name[..split];
        key = name[(split + 1)..];
        return true;
    }

    /// <summary>Appends the author's spelling of a cell key: a <c>$cell:row:key</c> indirection reads back as
    /// <c>row[key]</c>, nesting as deep as the indirection goes, and a <c>$expr:</c> key is parenthesized exactly
    /// when that is what reads back as one.</summary>
    /// <param name="into">The builder to append to.</param>
    /// <param name="key">The key as the document carries it.</param>
    public static void AppendKey(StringBuilder into, string key) {
        ArgumentNullException.ThrowIfNull(argument: into);
        ArgumentNullException.ThrowIfNull(argument: key);

        if (key.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.ExpressionKeyPrefix
        )) {
            // Inside [ ] the simple forms win: a bare name is a literal key, a number is a numeric one, and a
            // name indexed once more is the `$cell:` indirection. An expression key whose own text spells one
            // of those prints parenthesized, which is the one spelling that reads back as an expression key.
            var text = key[RuleFacts.ExpressionKeyPrefix.Length..];

            into.Append(value: ((TryParseKey(
                error: out _,
                key: out var reparsed,
                text: text
            ) && !reparsed.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.ExpressionKeyPrefix
            ))
                ? $"({text})"
                : text));
            return;
        }
        if (key.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.CellKeyPrefix
        )) {
            var rest = key[RuleFacts.CellKeyPrefix.Length..];
            var colon = rest.IndexOf(value: ':');

            if (
                (colon > 0) &&
                (colon < (rest.Length - 1))
            ) {
                into.Append(value: QuoteName(name: rest[..colon])).Append(value: '[');
                AppendKey(
                    into: into,
                    key: rest[(colon + 1)..]
                );
                into.Append(value: ']');
                return;
            }
        }
        into.Append(value: ((IsBareName(name: key) || IsNumberLexeme(text: key))
            ? key
            : $"`{key}`"));
    }
    /// <summary>Returns the author's spelling of a cell key.</summary>
    /// <param name="key">The key as the document carries it.</param>
    /// <returns>The spelling a key reader folds back to <paramref name="key"/>.</returns>
    public static string PrintKey(string key) {
        var builder = new StringBuilder();

        AppendKey(
            into: builder,
            key: key
        );

        return builder.ToString();
    }
    /// <summary>Whether a name prints bare, without backquotes.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the name lexes as one bare identifier.</returns>
    public static bool IsBareName(string name) =>
        ((ScanBareName(
            start: 0,
            text: name
        ) == name.Length) && (name.Length > 0) && !Calls.ContainsKey(key: name) && !IsReservedCallName(name: name));

    // The names the parser reads as a call or a literal before it considers a row: the folds, the vector functions,
    // the board queries, and the two vector literal keywords. None of them is in the operator table's call list.
    private static bool IsReservedCallName(string name) => (IsFoldName(name: name) || (name is "dot" or "similarity" or "identical" or "boardShift" or "boardFill" or "boardImage" or "vector" or "embed"));
    // A fold binds its member to a plain variable: one bare name, undotted, unreserved, and not a function's.
    private static bool IsBinderName(string name) => (IsBareName(name: name) && !name.Contains(value: '.') && !name.StartsWith(value: '$'));

    /// <summary>Renders a program in the infix spelling, throwing when it is not a well-formed postfix program.</summary>
    /// <param name="program">The program.</param>
    /// <returns>The spelling.</returns>
    /// <exception cref="ArgumentException">The program underflows or leaves more than one value.</exception>
    public static string Print(ExpressionProgram program) =>
        (TryPrint(
            program: program,
            text: out var text
        )
            ? text
            : throw new ArgumentException(
                message: "the program is not a well-formed postfix expression",
                paramName: nameof(program)
            )
        );
    /// <summary>Renders an instruction list in the infix spelling, throwing when it is not a well-formed postfix
    /// program.</summary>
    /// <param name="instructions">The postfix instructions.</param>
    /// <returns>The spelling.</returns>
    /// <exception cref="ArgumentException">The list underflows or leaves more than one value.</exception>
    public static string Print(IReadOnlyList<Instruction> instructions) =>
        Print(program: new ExpressionProgram(Instructions: instructions));
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

        if (
            (start < 0) ||
            (start >= text.Length) ||
            !IsNameStart(character: text[start])
        ) {
            return 0;
        }
        var reserved = (text[start] == '$');
        var index = (start + 1);

        while (index < text.Length) {
            var character = text[index];

            if (IsNamePart(character: character)) {
                index++;
                continue;
            }
            if (
                reserved &&
                (character == ':') &&
                ((index + 1) < text.Length) &&
                (IsNamePart(character: text[(index + 1)]) || IsSignedSegment(
                index: (index + 1),
                text: text
            ))
            ) {
                index++;
                continue;
            }
            if (
                reserved &&
                (character == '-') &&
                (text[(index - 1)] == ':') &&
                ((index + 1) < text.Length) &&
                char.IsAsciiDigit(c: text[(index + 1)])
            ) {
                index++;
                continue;
            }
            if (
                reserved &&
                (character == '[')
            ) {
                var close = LiveZoneIndexEnd(
                    bracket: index,
                    start: start,
                    text: text
                );

                if (close > 0) {
                    index = (close + 1);
                    continue;
                }
            }
            break;
        }

        return (index - start);
    }
    /// <summary>Splits a candidate name at dot access's exactly-one-dot rule — the same rule <see cref="TryParse"/>
    /// applies to an unreserved, unquoted name (<c>row.key</c> becomes the state read <c>row[key]</c>). Excluding a
    /// reserved (<c>$</c>-prefixed) or backquoted name is the caller's own job, exactly as <see cref="TryParse"/>'s
    /// own parser does it before calling this; used by <c>WorldModuleNamespace</c>'s import-alias text rewrite so it
    /// reads a dotted name on the grammar's own terms instead of a second copy of the rule.</summary>
    /// <param name="name">The candidate name.</param>
    /// <param name="row">The part before the dot, on success.</param>
    /// <param name="key">The literal key after the dot, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> splits cleanly at one dot; <see
    /// langword="false"/> — leaving <paramref name="row"/>/<paramref name="key"/> empty — when it carries no dot, or
    /// carries one it cannot split on (trailing, or more than one), which a text rewriter should leave untouched and
    /// let compilation report.</returns>
    public static bool TrySplitDottedName(string name, out string row, out string key) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return TrySplitDot(
            error: out _,
            key: out key,
            name: name,
            row: out row
        );
    }
    /// <summary>Parses an infix spelling to a program.</summary>
    /// <param name="text">The spelling.</param>
    /// <param name="program">The program, when the spelling parses.</param>
    /// <param name="error">Why it did not, naming the character position, or empty.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a well-formed expression.</returns>
    public static bool TryParse(string? text, out ExpressionProgram program, out string error) {
        program = EmptyProgram;
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
            var builder = new ProgramBuilder();
            var list = new List<Instruction>();

            root.Emit(
                builder: builder,
                into: list
            );
            program = new ExpressionProgram(Instructions: list) { Subprograms = builder.Subprograms };
            error = string.Empty;
            return true;
        } catch (SyntaxException failure) {
            error = failure.Message;
            return false;
        }
    }
    /// <summary>Parses the text between a name's brackets as a cell key, on the same terms as a key inside
    /// <see cref="TryParse"/>: <c>row[key]</c> spells a <c>$cell:</c> indirection, a bare name or reserved token
    /// (<c>$each</c>, <c>$local:&lt;name&gt;</c>, <c>$cell:&lt;row&gt;:&lt;key&gt;</c>) passes through, and any other
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
    /// <summary>Parses an authored vector operand (state cell, vector literal, or embed literal).</summary>
    /// <param name="text">The spelling.</param>
    /// <param name="token">The vector operand token, on success.</param>
    /// <param name="error">Why it did not, naming the character position, or empty.</param>
    public static bool TryParseVector(string? text, out VectorOperand token, out string error) {
        token = default!;
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
            token = ConvertToVectorOperand(node: root);
            error = string.Empty;
            return true;
        } catch (SyntaxException failure) {
            error = failure.Message;
            return false;
        }
    }
    /// <summary>Renders a program in the infix spelling <see cref="TryParse"/> reads back to the same program, with
    /// only the parentheses precedence requires.</summary>
    /// <param name="program">The program.</param>
    /// <param name="text">The spelling, when the program is well-formed.</param>
    /// <returns><see langword="false"/> when the program underflows, leaves more than one value, or carries an
    /// instruction the infix grammar does not spell — a <see cref="ExpressionOp.Call"/> into a shared subprogram,
    /// which only the lowering that built it can name.</returns>
    public static bool TryPrint(ExpressionProgram program, out string text) =>
        TryPrint(
            parentLevel: 0,
            program: program,
            text: out text
        );
    /// <summary>Renders a program as one operand of a comparison, parenthesized exactly when its own top-level
    /// operator binds looser than a comparison and the joined text would otherwise re-parse as a different
    /// program.</summary>
    /// <param name="program">The program.</param>
    /// <param name="text">The spelling, when the program is well-formed.</param>
    /// <returns><see langword="false"/> on <see cref="TryPrint(ExpressionProgram, out string)"/>'s terms.</returns>
    public static bool TryPrintComparisonOperand(ExpressionProgram program, out string text) =>
        TryPrint(
            parentLevel: ComparisonOperandLevel,
            program: program,
            text: out text
        );

    private static bool TryPrint(ExpressionProgram program, int parentLevel, out string text) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var stack = new Stack<Node>();

        foreach (var instruction in program.Instructions) {
            var node = Lower(
                binder: null,
                instruction: instruction,
                stack: stack,
                subprograms: program.Subprograms
            );

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

        stack.Pop().Print(
            into: builder,
            parentLevel: parentLevel,
            rightOperand: false
        );
        text = builder.ToString();
        return true;
    }

    /// <summary>Renders an instruction list in the infix spelling, on <see cref="TryPrint(ExpressionProgram, out string)"/>'s terms.</summary>
    /// <param name="instructions">The postfix instructions.</param>
    /// <param name="text">The spelling, when the list is a well-formed postfix program.</param>
    /// <returns><see langword="false"/> when the list is not one.</returns>
    public static bool TryPrint(IReadOnlyList<Instruction> instructions, out string text) =>
        TryPrint(
            program: new ExpressionProgram(Instructions: instructions),
            text: out text
        );

    // Collects the subprograms a fold body emits into, so one parse yields the whole program.
    private sealed class ProgramBuilder {
        public List<Subprogram> Subprograms { get; } = [];

        public int Add(string name, int arity, List<Instruction> instructions) {
            Subprograms.Add(item: new Subprogram(
                Arity: arity,
                Instructions: instructions,
                Name: name
            ));
            return (Subprograms.Count - 1);
        }
    }
    private abstract record Node {
        public abstract int Level { get; }

        public abstract void Emit(List<Instruction> into, ProgramBuilder builder);
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
        public abstract void PrintBare(StringBuilder into);
    }
    private sealed record Literal(decimal Value) : Node {
        public override int Level => PrimaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) => into.Add(item: Instruction.Constant(value: Value));
        public override void PrintBare(StringBuilder into) => into.Append(value: Value.ToString(provider: CultureInfo.InvariantCulture));
    }
    private sealed record StateRead(string Name, string? Key, bool Quoted = false) : Node {
        public override int Level => PrimaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) => into.Add(item: Instruction.Operand(
            key: Key,
            name: Name
        ));
        public override void PrintBare(StringBuilder into) {
            var (name, key) = (((Key is null) && TrySplitTableKey(
                Name,
                out var table,
                out var tableKey
            ))
                ? (table, tableKey)
                : (Name, Key)
            );
            into.Append(value: (Quoted
                ? $"`{name}`"
                : QuoteName(name: name)
            ));
            if (key is { } spelled) {
                into.Append(value: '[');
                AppendKey(
                    into: into,
                    key: spelled
                );
                into.Append(value: ']');
            }
        }
    }
    private sealed record VectorLiteral(string Value) : Node {
        public override int Level => PrimaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) =>
            throw new SyntaxException(message: "a vector literal cannot appear as a scalar expression operand");
        public override void PrintBare(StringBuilder into) {
            into.Append(value: "vector(\"").Append(value: Value).Append(value: "\")");
        }
    }
    private sealed record EmbedLiteral(string Text, string? Space = null) : Node {
        public override int Level => PrimaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) =>
            throw new SyntaxException(message: "an embed literal cannot appear as a scalar expression operand");
        public override void PrintBare(StringBuilder into) {
            // KEEP IN SYNC with the lexer's double-quoted string rule: it unescapes exactly \\ and \".
            into.Append(value: "embed(\"").Append(value: Text.Replace(newValue: "\\\\", oldValue: "\\").Replace(newValue: "\\\"", oldValue: "\"")).Append(value: '"');
            if (Space is not null) {
                into.Append(value: ", space: ").Append(value: QuoteName(name: Space));
            }
            into.Append(value: ')');
        }
    }
    private sealed record Unary(string Operator, Node Operand) : Node {
        public override int Level => UnaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) {
            Operand.Emit(
                builder: builder,
                into: into
            );
            into.Add(item: Instruction.Of(operation: ((Operator == "-")
                ? ExpressionOp.Negate
                : ExpressionOp.BitNot)));
        }
        public override void PrintBare(StringBuilder into) {
            into.Append(value: Operator);
            // A unary or negative-literal operand is parenthesized so "- -a" never prints as "--a".
            var wrap = ((Operand is Unary) || (Operand is Literal { Value: < 0m }));

            if (wrap) { into.Append(value: '('); }
            Operand.Print(
                into: into,
                parentLevel: UnaryLevel,
                rightOperand: false
            );
            if (wrap) { into.Append(value: ')'); }
        }
    }
    private sealed record Binary(string Operator, Node Left, Node Right) : Node {
        public override int Level => ExpressionSpelling.Level(symbol: Operator);

        public override void Emit(List<Instruction> into, ProgramBuilder builder) {
            Left.Emit(
                builder: builder,
                into: into
            );
            Right.Emit(
                builder: builder,
                into: into
            );
            into.Add(item: BinaryToken(symbol: Operator));
        }
        public override void PrintBare(StringBuilder into) {
            Left.Print(
                into: into,
                parentLevel: Level,
                rightOperand: false
            );
            into.Append(value: ' ').Append(value: Operator).Append(value: ' ');
            Right.Print(
                into: into,
                parentLevel: Level,
                rightOperand: true
            );
        }
    }
    private sealed record Ternary(Node Condition, Node WhenTrue, Node WhenFalse) : Node {
        public override int Level => TernaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) {
            Condition.Emit(
                builder: builder,
                into: into
            );
            WhenTrue.Emit(
                builder: builder,
                into: into
            );
            WhenFalse.Emit(
                builder: builder,
                into: into
            );
            into.Add(item: Instruction.Of(operation: ExpressionOp.Select));
        }
        public override void PrintBare(StringBuilder into) {
            Condition.Print(
                into: into,
                parentLevel: (TernaryLevel + 1),
                rightOperand: false
            );
            into.Append(value: " ? ");
            WhenTrue.Print(
                into: into,
                parentLevel: (TernaryLevel + 1),
                rightOperand: false
            );
            into.Append(value: " : ");
            WhenFalse.Print(
                into: into,
                parentLevel: TernaryLevel,
                rightOperand: false
            );
        }
    }
    private sealed record Call(string Name, Node[] Arguments, string[] Names) : Node {
        public override int Level => PrimaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) {
            if (Name is "dot" or "similarity" or "identical") {
                if ((Arguments.Length != 2) || (Names.Length != 0)) {
                    throw new SyntaxException(message: $"'{Name}' takes exactly 2 vector arguments");
                }
                into.Add(item: Instruction.Vector(
                    left: ConvertToVectorOperand(node: Arguments[0]),
                    operation: (Name switch {
                        "dot" => ExpressionOp.Dot,
                        "similarity" => ExpressionOp.Similarity,
                        _ => ExpressionOp.Identical,
                    }),
                    right: ConvertToVectorOperand(node: Arguments[1])
                ));
                return;
            }

            foreach (var argument in Arguments) {
                argument.Emit(
                    builder: builder,
                    into: into
                );
            }
            into.Add(item: (Name switch {
                "boardShift" => Instruction.Board(
                index: Names[1],
                operation: ExpressionOp.BoardShift,
                topology: Names[0]
            ),
                "boardFill" => Instruction.Board(
                index: Names[1],
                operation: ExpressionOp.BoardFill,
                topology: Names[0]
            ),
                "boardImage" => Instruction.Board(
                index: Names[1],
                operation: ExpressionOp.BoardImage,
                topology: Names[0]
            ),
                _ => Instruction.Of(operation: Calls[Name].Operation),
            }));
        }
        public override void PrintBare(StringBuilder into) {
            into.Append(value: Name).Append(value: '(');
            for (var index = 0; (index < Arguments.Length); index++) {
                if (index > 0) { into.Append(value: ", "); }
                Arguments[index].Print(
                    into: into,
                    parentLevel: 0,
                    rightOperand: false
                );
            }
            foreach (var name in Names) {
                into.Append(value: ", ").Append(value: QuoteName(name: name));
            }
            into.Append(value: ')');
        }
    }
    // The member a fold binds, read inside the fold's body by the binder's own name.
    private sealed record Member(string Name) : Node {
        public override int Level => PrimaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) => into.Add(item: Instruction.Of(operation: ExpressionOp.Member));
        public override void PrintBare(StringBuilder into) => into.Append(value: Name);
    }
    private sealed record FoldCall(string Name, string Family, string Binder, Node Body) : Node {
        public override int Level => PrimaryLevel;

        public override void Emit(List<Instruction> into, ProgramBuilder builder) {
            var body = new List<Instruction>();

            Body.Emit(
                builder: builder,
                into: body
            );
            into.Add(item: Instruction.Fold(
                binder: Binder,
                family: Family,
                operation: FoldOperation(name: Name),
                subprogram: builder.Add(
                    arity: 0,
                    instructions: body,
                    name: Name
                )
            ));
        }
        public override void PrintBare(StringBuilder into) {
            into.Append(value: Name).Append(value: '(').Append(value: QuoteName(name: Family)).Append(value: ", ").Append(value: Binder).Append(value: " -> ");
            Body.Print(
                into: into,
                parentLevel: 0,
                rightOperand: false
            );
            into.Append(value: ')');
        }
    }

    private static VectorOperand ConvertToVectorOperand(Node node) => node switch {
        StateRead state => new VectorOperand.Cell(Name: state.Name, Key: state.Key),
        VectorLiteral vec => new VectorOperand.Literal(Value: vec.Value),
        EmbedLiteral embed => new VectorOperand.Embed(Text: embed.Text, Space: embed.Space),
        _ => throw new SyntaxException(message: $"argument to vector function must be a state read, vector literal, or embed literal, found '{node.GetType().Name}'")
    };
    private static Node LowerVectorOperand(VectorOperand operand) => operand switch {
        VectorOperand.Cell cell => new StateRead(Name: cell.Name, Key: cell.Key),
        VectorOperand.Literal lit => new VectorLiteral(Value: lit.Value),
        VectorOperand.Embed embed => new EmbedLiteral(Text: embed.Text, Space: embed.Space),
        _ => throw new InvalidOperationException()
    };

    private sealed class SyntaxException(string message) : Exception(message: message);
    private enum Lexeme : byte { End, Number, Name, Punctuation, String }
    // A recursive-descent parser over a one-token lookahead lexer; the grammar is small enough that the two live in
    // one class and the token stream is never materialized.
    private sealed class Parser(string text) {
        // Longest first, so ">>>" wins over ">>" over ">".
        private static readonly string[] Punctuations = [">>>", "<<", ">>", "==", "!=", "<=", ">=", "<", ">", "+", "->", "-", "*", "/", "%", "&", "|", "^", "~", "??", "?", ":", "(", ")", "[", "]", ","];

        private string? m_binder;
        private int m_depth;
        private Lexeme m_kind;
        private int m_position;
        private bool m_primed;
        private bool m_quoted;
        private int m_start;
        private string m_value = string.Empty;

        private bool Accept(string punctuation) {
            Prime();
            if (
                (m_kind == Lexeme.Punctuation) &&
                (m_value == punctuation)
            ) {
                Advance();
                return true;
            }
            return false;
        }
        private void Advance() {
            while (
                (m_position < text.Length) &&
                char.IsWhiteSpace(c: text[m_position])
            ) {
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

                if (
                    (character == '0') &&
                    ((end + 1) < text.Length) &&
                    ((text[(end + 1)] == 'x') || (text[(end + 1)] == 'X'))
                ) {
                    end += 2;
                    while (
                        (end < text.Length) &&
                        char.IsAsciiHexDigit(c: text[end])
                    ) { end++; }
                } else {
                    while (
                        (end < text.Length) &&
                        (char.IsAsciiDigit(c: text[end]) || (text[end] == '.'))
                    ) { end++; }
                }
                m_kind = Lexeme.Number;
                m_value = text[m_position..end];
                m_position = end;
                return;
            }
            if (character == '"') {
                var cursor = (m_position + 1);
                StringBuilder? unescaped = null;
                var segmentStart = cursor;

                while (true) {
                    if (cursor >= text.Length) {
                        throw Fail(message: "a double-quoted string is not closed");
                    }
                    var current = text[cursor];

                    if (current == '"') {
                        break;
                    }
                    if (current == '\\') {
                        if ((cursor + 1) >= text.Length) {
                            throw Fail(message: "a double-quoted string is not closed");
                        }
                        var escaped = text[(cursor + 1)];

                        if (escaped is not ('"' or '\\')) {
                            throw Fail(message: $"a double-quoted string admits only the escapes \\\" and \\\\, not \\{escaped}");
                        }
                        unescaped ??= new StringBuilder();
                        unescaped.Append(count: (cursor - segmentStart), startIndex: segmentStart, value: text).Append(value: escaped);
                        cursor += 2;
                        segmentStart = cursor;
                        continue;
                    }
                    cursor++;
                }
                m_kind = Lexeme.String;
                m_value = ((unescaped is null)
                    ? text[(m_position + 1)..cursor]
                    : unescaped.Append(count: (cursor - segmentStart), startIndex: segmentStart, value: text).ToString()
                );
                m_quoted = true;
                m_position = (cursor + 1);
                return;
            }
            if (character == '`') {
                var close = text.IndexOf(
                    startIndex: (m_position + 1),
                    value: '`'
                );

                if (close < 0) {
                    throw Fail(message: "a backquoted name is not closed");
                }
                m_kind = Lexeme.Name;
                m_value = text[(m_position + 1)..close];
                m_quoted = true;
                m_position = (close + 1);
                if (m_value.Length == 0) {
                    throw Fail(message: "a backquoted name is empty");
                }
                return;
            }
            if (IsNameStart(character: character)) {
                var reserved = (character == '$');
                var end = (m_position + 1);

                while (end < text.Length) {
                    if (IsNamePart(character: text[end])) {
                        end++;
                        continue;
                    }
                    if (
                        reserved &&
                        (text[end] == ':') &&
                        ((end + 1) < text.Length) &&
                        IsNamePart(character: text[(end + 1)])
                    ) {
                        end++;
                        continue;
                    }
                    if (
                        reserved &&
                        (text[end] == ':') &&
                        IsSignedSegment(
                        index: (end + 1),
                        text: text
                    )
                    ) {
                        end += 2;
                        continue;
                    }
                    if (
                        reserved &&
                        (LiveZoneIndexEnd(
                        bracket: end,
                        start: m_position,
                        text: text
                    ) is var close) &&
                        (close > 0)
                    ) {
                        end = (close + 1);
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
                if (string.CompareOrdinal(
                    strA: text,
                    indexA: m_position,
                    strB: punctuation,
                    indexB: 0,
                    length: punctuation.Length
                ) == 0) {
                    m_kind = Lexeme.Punctuation;
                    m_value = punctuation;
                    m_position += punctuation.Length;
                    return;
                }
            }
            throw Fail(message: $"unexpected character '{character}'");
        }
        private static string? BinaryOperator(string symbol) => symbol switch {
            "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "<<" or ">>" or ">>>" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "??" => symbol,
            _ => null,
        };
        private void Expect(string punctuation) {
            if (!Accept(punctuation: punctuation)) {
                throw Fail(message: $"expected '{punctuation}'{Found()}");
            }
        }
        private void Descend() {
            if (++m_depth > MaxNesting) {
                throw Fail(message: $"the expression nests more than {MaxNesting} deep");
            }
        }
        private SyntaxException Fail(string message) => new(message: $"at character {(m_start + 1)}: {message}");
        private string Found() {
            Prime();
            return m_kind switch {
                Lexeme.End => " but reached the end",
                _ => $" but found '{m_value}'",
            };
        }
        private bool KeyEnds() => ((m_kind == Lexeme.End) || ((m_kind == Lexeme.Punctuation) && (m_value == "]")));
        // Precedence climbing over the binary table: every operator is left-associative.
        private Node ParseBinary(int minimumLevel) {
            var left = ParseUnary();
            var spine = 0;

            while (true) {
                Prime();
                if (
                    (m_kind != Lexeme.Punctuation) ||
                    (BinaryOperator(symbol: m_value) is not { } symbol)
                ) {
                    m_depth -= spine;

                    return left;
                }
                var level = Level(symbol: symbol);

                if (level < minimumLevel) {
                    m_depth -= spine;

                    return left;
                }
                Advance();
                // Each operator of a run puts what came before it one level further down the left of the tree.
                Descend();
                spine++;
                var right = ParseBinary(minimumLevel: (level + 1));

                left = new Binary(
                    Left: left,
                    Operator: symbol,
                    Right: right
                );
            }
        }
        private Node ParseCall(string name, int arity, int names) {
            Expect(punctuation: "(");
            var arguments = new Node[arity];

            for (var index = 0; (index < arity); index++) {
                if (index > 0) { Expect(punctuation: ","); }
                arguments[index] = ParseExpression();
            }
            var extra = new string[names];

            for (var index = 0; (index < names); index++) {
                Expect(punctuation: ",");
                Prime();
                if (m_kind != Lexeme.Name) {
                    throw Fail(message: $"'{name}' takes a name here{Found()}");
                }
                extra[index] = m_value;
                Advance();
            }
            if (Accept(punctuation: ",")) {
                throw Fail(message: $"'{name}' takes {(arity + names)} argument(s)");
            }
            Expect(punctuation: ")");
            return new Call(
                Arguments: arguments,
                Name: name,
                Names: extra
            );
        }
        private Node ParseFold(string name) {
            Expect(punctuation: "(");
            Prime();
            if (m_kind != Lexeme.Name) {
                throw Fail(message: $"'{name}' folds a family named here{Found()}");
            }
            var family = m_value;

            Advance();
            Expect(punctuation: ",");
            Prime();
            if (
                (m_kind != Lexeme.Name) ||
                m_quoted ||
                !IsBinderName(name: m_value)
            ) {
                throw Fail(message: $"'{name}' binds its member to a bare, undotted, unreserved name here{Found()}");
            }
            var binder = m_value;

            Advance();
            Expect(punctuation: "->");
            if (m_binder is not null) {
                throw Fail(message: $"'{name}' nests inside the fold binding '{m_binder}'; a fold nests at most once");
            }
            m_binder = binder;

            Node body;

            try {
                body = ParseExpression();
            } finally {
                m_binder = null;
            }
            Expect(punctuation: ")");
            return new FoldCall(
                Binder: binder,
                Body: body,
                Family: family,
                Name: name
            );
        }
        private decimal ParseNumber(string lexeme, int start) {
            if (lexeme.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "0x"
            )) {
                if (
                    (lexeme.Length == 2) ||
                    !ulong.TryParse(
                    s: lexeme.AsSpan(start: 2),
                    style: NumberStyles.AllowHexSpecifier,
                    provider: CultureInfo.InvariantCulture,
                    result: out var bits
                )
                ) {
                    throw new SyntaxException(message: $"at character {(start + 1)}: '{lexeme}' is not a hexadecimal literal of at most 16 digits");
                }
                return bits;
            }
            if (!decimal.TryParse(
                s: lexeme,
                style: NumberStyles.AllowDecimalPoint,
                provider: CultureInfo.InvariantCulture,
                result: out var value
            )) {
                throw new SyntaxException(message: $"at character {(start + 1)}: '{lexeme}' is not a number");
            }
            return value;
        }
        private Node ParsePrimary() {
            Prime();
            switch (m_kind) {
                case Lexeme.Number: {
                        var lexeme = m_value;
                        var start = m_start;

                        Advance();
                        return new Literal(Value: ParseNumber(
                            lexeme: lexeme,
                            start: start
                        ));
                    }
                case Lexeme.Name: {
                        var name = m_value;
                        var quoted = m_quoted;

                        Advance();
                        if (!quoted && (name == "vector")) {
                            if (Accept(punctuation: "(")) {
                                Prime();
                                if (m_kind != Lexeme.String) {
                                    throw Fail(message: "vector literal expects a double-quoted base64url string");
                                }
                                var vecBase64 = m_value;

                                Advance();
                                Expect(punctuation: ")");
                                return new VectorLiteral(Value: vecBase64);
                            }
                        }
                        if (!quoted && (name == "embed")) {
                            if (Accept(punctuation: "(")) {
                                Prime();
                                if (m_kind != Lexeme.String) {
                                    throw Fail(message: "embed literal expects a double-quoted string");
                                }
                                var embedText = m_value;

                                Advance();
                                string? embedSpace = null;

                                if (Accept(punctuation: ",")) {
                                    Prime();
                                    if ((m_kind == Lexeme.Name) && (m_value == "space")) {
                                        Advance();
                                        Expect(punctuation: ":");
                                        Prime();
                                        if ((m_kind == Lexeme.Name) || (m_kind == Lexeme.String)) {
                                            embedSpace = m_value;
                                            Advance();
                                        } else {
                                            throw Fail(message: "embed literal space expects an identifier or string");
                                        }
                                    } else {
                                        throw Fail(message: "expected 'space:' in embed literal");
                                    }
                                }
                                Expect(punctuation: ")");
                                return new EmbedLiteral(Space: embedSpace, Text: embedText);
                            }
                        }
                        if (!quoted && (name is "dot" or "similarity" or "identical")) {
                            return ParseCall(
                                arity: 2,
                                name: name,
                                names: 0
                            );
                        }
                        if (
                            !quoted &&
                            IsFoldName(name: name)
                        ) {
                            return ParseFold(name: name);
                        }
                        if (
                            !quoted &&
                            Calls.TryGetValue(
                            key: name,
                            value: out var call
                        )
                        ) {
                            return ParseCall(
                                name: name,
                                arity: call.Arity,
                                names: 0
                            );
                        }
                        if (
                            !quoted &&
                            (name is "boardShift" or "boardFill" or "boardImage")
                        ) {
                            return ParseCall(
                                arity: 1,
                                name: name,
                                names: 2
                            );
                        }
                        if (
                            !quoted &&
                            Accept(punctuation: "(")
                        ) {
                            throw Fail(message: $"'{name}' is not a function; a state read is a bare name (or `{name}` to read a row of that name)");
                        }
                        if (
                            !quoted &&
                            string.Equals(
                            a: name,
                            b: m_binder,
                            comparisonType: StringComparison.Ordinal
                        )
                        ) {
                            return new Member(Name: name);
                        }
                        var stateName = name;
                        string? key = null;

                        // An unreserved, unquoted "a.b" is the state read "a[b]" — a reserved ($) name keeps its
                        // dotted segments unchanged, and a backquoted name is never split.
                        if (
                            !quoted &&
                            !name.StartsWith(value: '$')
                        ) {
                            if (
                                TrySplitDot(
                                error: out var dotError,
                                key: out var dotKey,
                                name: name,
                                row: out var dotRow
                            )
                            ) {
                                stateName = dotRow;
                                key = dotKey;
                            } else if (dotError is not null) {
                                throw Fail(message: dotError);
                            }
                        }
                        if (Accept(punctuation: "[")) {
                            if (key is not null) {
                                throw Fail(message: $"'{name}' already names a key with '.'; a dotted read does not also take '[...]'");
                            }
                            key = ParseKey();
                            Expect(punctuation: "]");
                        }
                        if (
                            (key is not null) &&
                            !quoted &&
                            stateName.StartsWith(
                            comparisonType: StringComparison.Ordinal,
                            value: RuleFacts.TablePrefix
                        )
                        ) {
                            return new StateRead(
                                Key: null,
                                Name: $"{stateName}:{key}"
                            );
                        }
                        return new StateRead(
                            Key: key,
                            Name: stateName
                        );
                    }
                case Lexeme.Punctuation when (m_value == "("): {
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
        private Node ParseTernary() {
            Descend();

            var condition = ParseBinary(minimumLevel: 2);

            if (!Accept(punctuation: "?")) {
                m_depth--;

                return condition;
            }
            var whenTrue = ParseTernary();

            Expect(punctuation: ":");
            var whenFalse = ParseTernary();

            m_depth--;

            return new Ternary(
                Condition: condition,
                WhenFalse: whenFalse,
                WhenTrue: whenTrue
            );
        }
        private Node ParseUnary() {
            if (Accept(punctuation: "-")) {
                Descend();

                var operand = ParseUnary();

                m_depth--;

                return ((operand is Literal literal)
                    ? new Literal(Value: -literal.Value)
                    : new Unary(
                        Operand: operand,
                        Operator: "-"
                    )
                );
            }
            if (Accept(punctuation: "~")) {
                Descend();

                var inverted = ParseUnary();

                m_depth--;

                return new Unary(
                    Operator: "~",
                    Operand: inverted
                );
            }
            return ParsePrimary();
        }
        private void Prime() {
            if (!m_primed) {
                Advance();
                m_primed = true;
            }
        }

        public void ExpectEnd() {
            Prime();
            if (m_kind != Lexeme.End) {
                throw Fail(message: $"unexpected '{m_value}' after the expression");
            }
        }
        public Node ParseExpression() => ParseTernary();
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
                if (
                    indexable &&
                    Accept(punctuation: "[")
                ) {
                    Descend();

                    var inner = ParseKey();

                    m_depth--;
                    Expect(punctuation: "]");
                    Prime();
                    if (
                        KeyEnds() &&
                        !inner.StartsWith(
                        comparisonType: StringComparison.Ordinal,
                        value: RuleFacts.ExpressionKeyPrefix
                    )
                    ) {
                        return $"{RuleFacts.CellKeyPrefix}{key}:{inner}";
                    }
                }
                (m_position, m_kind, m_value, m_quoted, m_start, m_primed) = saved;
            }
            var node = ParseExpression();
            var builder = new ProgramBuilder();
            var instructions = new List<Instruction>();

            node.Emit(
                builder: builder,
                into: instructions
            );
            return $"{RuleFacts.ExpressionKeyPrefix}{Print(program: new ExpressionProgram(Instructions: instructions) { Subprograms = builder.Subprograms })}";
        }
    }
}
