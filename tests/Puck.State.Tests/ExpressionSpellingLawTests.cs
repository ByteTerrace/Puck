using Xunit;

namespace Puck.State.Tests;

/// <summary>The infix spelling is syntax over the IR: C precedence parses to the instructions an author would write
/// by hand, and every instruction prints to a spelling that parses back to itself with only the parentheses
/// precedence needs. The world document's converter and schema facts live in <c>tests/Puck.World.Schema.Tests</c>.</summary>
public sealed class ExpressionSpellingLawTests {
    private static Instruction C(decimal value) => Instruction.Constant(value: value);
    private static IReadOnlyList<Instruction> Parse(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out var parsed,
                text: text
            ),
            userMessage: error
        );
        return parsed.Instructions;
    }
    private static Instruction S(string name, string? key = null) => Instruction.Operand(
        key: key,
        name: name
    );
    private static string Spell(Instruction token) => token switch {
        { Payload: InstructionPayload.State state } => state.Name,
        { Payload: InstructionPayload.Constant constant } => constant.Value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        _ => (char.ToLowerInvariant(c: token.Operation.ToString()[0]) + token.Operation.ToString()[1..]),
    };

    // Printing is the inverse of parsing even where a name would bind differently bare: a row sharing the fold
    // binder's name, and a row named like a function the parser reads before it considers a row.
    [Theory]
    [InlineData("sum(samples, x -> `x`)")]
    [InlineData("sum(samples, x -> `x`[k] + x)")]
    [InlineData("`sum` + 1")]
    [InlineData("`count` * `any`")]
    [InlineData("`dot` - `boardShift`")]
    [InlineData("`vector` + `embed`")]
    public void ANameThatWouldBindDifferentlyBareKeepsItsBackquotes(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out var program,
                text: text
            ),
            userMessage: error
        );
        Assert.True(condition: ExpressionSpelling.TryPrint(
            program: program,
            text: out var printed
        ));
        Assert.Equal(
            actual: printed,
            expected: text
        );
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out error,
                program: out var reparsed,
                text: printed
            ),
            userMessage: error
        );
        Assert.Equal(
            actual: reparsed.Instructions,
            expected: program.Instructions
        );
        Assert.Equal(
            actual: reparsed.Subprograms.Select(selector: static subprogram => subprogram.Instructions),
            expected: program.Subprograms.Select(selector: static subprogram => subprogram.Instructions)
        );
    }
    // A fold binder is one plain variable: the parser refuses a dotted, reserved, or function-named one, and the
    // printer refuses a program carrying one rather than print text the parser would reject.
    [Theory]
    [InlineData("sum(samples, a.b -> a.b)")]
    [InlineData("sum(samples, count -> 1)")]
    [InlineData("sum(samples, minimum -> 1)")]
    public void ABinderTheParserWouldRefuseNeitherParsesNorPrints(string text) {
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out var error,
            program: out _,
            text: text
        ));
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "bare, undotted, unreserved name"
        );
        Assert.False(condition: ExpressionSpelling.TryPrint(
            program: new ExpressionProgram(Instructions: [Instruction.Fold(
                binder: "a.b",
                family: "samples",
                operation: ExpressionOp.Sum,
                subprogram: 0
            )]) {
                Subprograms = [new Subprogram(
                    Arity: 0,
                    Instructions: [Instruction.Of(operation: ExpressionOp.Member)],
                    Name: "sum"
                )],
            },
            text: out _
        ));
    }
    [Fact]
    public void AMalformedPostfixListDoesNotPrint() {
        Assert.False(condition: ExpressionSpelling.TryPrint(
            [Instruction.Of(operation: ExpressionOp.Add)],
            out _
        ));
        Assert.False(condition: ExpressionSpelling.TryPrint(
            [C(value: 1m), C(value: 2m)],
            out _
        ));
    }
    [InlineData("", "empty")]
    [InlineData("a +", "reached the end")]
    [InlineData("foo(1)", "not a function")]
    [InlineData("minimum(1)", "expected ','")]
    [InlineData("minimum(1, 2, 3)", "argument")]
    [InlineData("1 2", "unexpected '2'")]
    [InlineData("`open", "not closed")]
    [InlineData("a ? b", "expected ':'")]
    [InlineData("a # b", "unexpected character '#'")]
    [InlineData("boardShift(m, board)", "expected ','")]
    [Theory]
    public void AMalformedSpellingIsRefusedByName(string text, string expected) {
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out var error,
            text: text,
            program: out _
        ));
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: expected
        );
    }
    // row.key and row[key] must parse to the identical instruction: dot access is syntax over the same state read,
    // never a second representation.
    [Fact]
    public void ADottedReadParsesToTheIdenticalTokenAsItsBracketForm() {
        Assert.Equal(
            Parse(text: "vitals[mana]"),
            Parse(text: "vitals.mana")
        );
        Assert.Equal(
            [S(
                    key: "mana",
                    name: "vitals"
                )],
            Parse(text: "vitals.mana")
        );
    }
    // A decimal literal's own dot is never mistaken for dot access: the lexer decides name-vs-number from the
    // first character alone, so "0.25" is one constant token regardless of what follows.
    [Fact]
    public void ADecimalLiteralsDotIsUnaffectedByDotAccess() {
        Assert.Equal(
            [C(value: 0.25m)],
            Parse(text: "0.25")
        );
        Assert.Equal(
            [S("a"), C(value: 0.25m), Instruction.Of(operation: ExpressionOp.Add)],
            Parse(text: "a + 0.25")
        );
    }
    // A reserved ($) name keeps every dotted segment it carries — the dot-access split never applies to it, even
    // when it is used as the row half of what would otherwise be a dotted read.
    [Fact]
    public void AReservedNameKeepsItsDottedSegmentsUnchanged() {
        Assert.Equal(
            [S("$local:x.y")],
            Parse(text: "$local:x.y")
        );
        Assert.Equal(
            "$local:x.y",
            ExpressionSpelling.Print(instructions: [S("$local:x.y")])
        );
    }
    // A backquoted name is never split at a dot, even one carrying a literal dot character.
    [Fact]
    public void ABackquotedNameIsNeverSplitAtADot() {
        Assert.Equal(
            [S("seat.one")],
            Parse(text: "`seat.one`")
        );
        Assert.Equal(
            "`seat.one`",
            ExpressionSpelling.Print(instructions: [S("seat.one")])
        );
    }
    // The key half of a dotted read may be a numeric key, exactly as bracket form admits one.
    [Fact]
    public void ADottedReadAdmitsANumericKeyTheSameAsBracketForm() {
        Assert.Equal(
            Parse(text: "board[5]"),
            Parse(text: "board.5")
        );
        Assert.Equal(
            [S(
                    key: "5",
                    name: "board"
                )],
            Parse(text: "board.5")
        );
    }
    // A trailing dot is refused by name — a dotted read is "row.key", and a dot with nothing after it is a parse
    // error naming the fix rather than a name that happens to end in a period.
    [Fact]
    public void ATrailingDotIsRefusedByName() {
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out var error,
            text: "vitals.",
            program: out _
        ));
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "ends with a dot"
        );
    }
    // More than one dot is refused by name, naming the bracket-form fix — a dotted read admits exactly one dot.
    [Fact]
    public void MoreThanOneDotIsRefusedByNameNamingTheBracketFix() {
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out var error,
            text: "vitals.mana.max",
            program: out _
        ));
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "more than one dot"
        );
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "vitals[mana.max]"
        );
    }
    // A dynamic key wearing dot syntax ("row.$each") is refused by name — a literal key alone may spell as a dot;
    // a dynamic key still needs bracket form.
    [Fact]
    public void ADottedKeyThatIsItselfReservedIsRefusedByNameNamingBracketForm() {
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out var error,
            text: "hp.$each",
            program: out _
        ));
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "hp[$each]"
        );
    }
    // Print always emits bracket form, even for a program parsed from a dotted spelling: there is exactly one
    // canonical printed form, though two admitted parsed ones.
    [Fact]
    public void PrintAlwaysEmitsBracketFormForADottedRead() {
        Assert.Equal(
            "vitals[mana]",
            ExpressionSpelling.Print(instructions: Parse(text: "vitals.mana"))
        );
    }
    // ExpressionSpelling.TrySplitDottedName is the same one-dot split ParsePrimary performs, exposed for a caller
    // (import renaming) that rewrites text rather than compiling it. Excluding a reserved or backquoted name is
    // documented as the CALLER's own job — this API splits whatever candidate name it is given.
    [Fact]
    public void TrySplitDottedNameSplitsTheSameWayParsingDoes() {
        Assert.True(condition: ExpressionSpelling.TrySplitDottedName(
            key: out var key,
            name: "vitals.mana",
            row: out var row
        ));
        Assert.Equal(
            actual: row,
            expected: "vitals"
        );
        Assert.Equal(
            actual: key,
            expected: "mana"
        );
        Assert.False(condition: ExpressionSpelling.TrySplitDottedName(
            key: out _,
            name: "plain",
            row: out _
        ));
        Assert.False(condition: ExpressionSpelling.TrySplitDottedName(
            key: out _,
            name: "trailing.",
            row: out _
        ));
    }
    [Fact]
    public void EveryContextFreeTokenHasOneRoundTrippingSpellingAndCompiles() {
        var context = new Rules.RuleCompileContext(
            null,
            StateCatalog.Compile(section: null),
            null,
            null,
            null,
            240,
            Rules.RuleVocabulary.Core
        );
        // Member reads the family a fold bound, so it has no spelling and no kind outside a fold body.
        var operations = Enum.GetValues<ExpressionOp>().Where(predicate: static operation => ((ExpressionOperators.PayloadOf(operation: operation) == PayloadShape.None) && (operation != ExpressionOp.Member))).ToArray();

        foreach (var operation in operations) {
            var token = Instruction.Of(operation: operation);
            IReadOnlyList<Instruction>? expression = null;

            for (var arity = 1; (arity <= 4); arity++) {
                Instruction[] candidate = [.. Enumerable.Repeat(
                        S("v"),
                        arity
                    ), token];

                if (!ExpressionSpelling.TryPrint(
                    instructions: candidate,
                    text: out var text
                )) { continue; }
                Assert.Equal(
                    candidate,
                    Parse(text: text)
                );
                Assert.Null(@object: expression);
                expression = candidate;
            }
            Assert.NotNull(@object: expression);
            var admitted = false;

            foreach (var kind in new[] { CellKind.Int, CellKind.Fixed }) {
                try {
                    _ = Rules.RuleCompiler.CompileExpression(
                        new ExpressionProgram(Instructions: [.. expression.Select(selector: item => ((item.Payload is InstructionPayload.State)
                        ? C(value: 1)
                        : item))]),
                        kind,
                        "catalog-law",
                        "catalog-law",
                        context
                    );
                    admitted = true;
                } catch (RuleException) { }
            }
            Assert.True(
                condition: admitted,
                userMessage: $"No numeric kind admits {operation}"
            );
        }
    }
    [Fact]
    public void NamesKeysCallsAndBoardOpsSpellTheirTokens() {
        Assert.Equal(
            [S(
                    key: "$each",
                    name: "hp"
                )],
            Parse(text: "hp[$each]")
        );
        Assert.Equal(
            [S(
                    key: "0",
                    name: "seat-1"
                )],
            Parse(text: "`seat-1`[0]")
        );
        Assert.Equal(
            [S("$table:armor:$each"), C(value: 2m), Instruction.Of(operation: ExpressionOp.Multiply)],
            Parse(text: "$table:armor:$each * 2")
        );
        Assert.Equal(
            [S("$board:mask:board:-6:-6"), C(value: 1m), Instruction.Of(operation: ExpressionOp.Subtract)],
            Parse(text: "$board:mask:board:-6:-6 - 1")
        );
        Assert.Equal(
            "$board:mask:board:-6:-6 - 1",
            ExpressionSpelling.Print(instructions: [S("$board:mask:board:-6:-6"), C(value: 1m), Instruction.Of(operation: ExpressionOp.Subtract)])
        );
        Assert.Equal(
            [S("$table:armor:$each")],
            Parse(text: "$table:armor[$each]")
        );
        Assert.Equal(
            [S("$table:moves:power:$local:move")],
            Parse(text: "$table:moves:power[$local:move]")
        );
        Assert.Equal(
            [S("$table:moves:power:$cell:turn:move")],
            Parse(text: "$table:moves:power[$cell:turn:move]")
        );
        Assert.Equal(
            [S("$table:armor:7")],
            Parse(text: "$table:armor[7]")
        );
        Assert.Equal(
            [S(
                    key: "$cell:minion:$each",
                    name: "buffs"
                )],
            Parse(text: "buffs[minion[$each]]")
        );
        Assert.Equal(
            [S(
                    key: "$cell:minion:$cell:squad:$each",
                    name: "buffs"
                )],
            Parse(text: "buffs[minion[squad[$each]]]")
        );
        Assert.Equal(
            [S("$table:t:$cell:minion:$each")],
            Parse(text: "$table:t[minion[$each]]")
        );
        Assert.Equal(
            "buffs[minion[$each]]",
            ExpressionSpelling.Print(instructions: [S(
                    key: "$cell:minion:$each",
                    name: "buffs"
                )])
        );
        Assert.Equal(
            "$table:moves:power[$local:move] + $table:armor[7]",
            ExpressionSpelling.Print(instructions: [S("$table:moves:power:$local:move"), S("$table:armor:7"), Instruction.Of(operation: ExpressionOp.Add)])
        );
        Assert.Equal(
            [S("damage"), S("hp"), Instruction.Of(operation: ExpressionOp.Minimum)],
            Parse(text: "minimum(damage, hp)")
        );
        Assert.Equal(
            [S("v"), C(value: 0m), C(value: 10m), Instruction.Of(operation: ExpressionOp.Clamp)],
            Parse(text: "clamp(v, 0, 10)")
        );
        Assert.Equal(
            [S("v"), C(value: 8m), C(value: 4m), Instruction.Of(operation: ExpressionOp.BitField)],
            Parse(text: "bitField(v, 8, 4)")
        );
        Assert.Equal(
            [S("m"), Instruction.Board(operation: ExpressionOp.BoardShift,
                    index: "north",
                    topology: "board"
                )],
            Parse(text: "boardShift(m, board, north)")
        );
        Assert.Equal(
            [S("m"), Instruction.Board(operation: ExpressionOp.BoardFill,
                    index: "north",
                    topology: "board"
                )],
            Parse(text: "boardFill(m, board, north)")
        );
        Assert.Equal(
            [S("m"), Instruction.Board(operation: ExpressionOp.BoardImage,
                    index: "rot180",
                    topology: "board"
                )],
            Parse(text: "boardImage(m, board, rot180)")
        );
        Assert.Equal(
            [S("m"), Instruction.Of(operation: ExpressionOp.PopCount)],
            Parse(text: "setBitCount(m)")
        );
        Assert.Equal(
            [S("c"), S("a"), S("b"), Instruction.Of(operation: ExpressionOp.Select)],
            Parse(text: "select(c, a, b)")
        );
    }
    [InlineData("a + b * c", "a", "b", "c", "multiply", "add")]
    [InlineData("(a + b) * c", "a", "b", "add", "c", "multiply")]
    [InlineData("a - b - c", "a", "b", "subtract", "c", "subtract")]
    [InlineData("a << 2 & b", "a", "2", "shiftLeft", "b", "bitAnd")]
    [InlineData("a == b | c", "a", "b", "equal", "c", "bitOr")]
    [InlineData("a >>> 1 ^ b", "a", "1", "shiftRightLogical", "b", "bitXor")]
    [InlineData("a % 3 >= b", "a", "3", "modulo", "b", "greaterOrEqual")]
    [Theory]
    public void PrecedenceFollowsC(string text, params string[] expected) {
        Assert.Equal(
            expected,
            Parse(text: text).Select(selector: Spell)
        );
    }
    [InlineData("a + b * c")]
    [InlineData("(a + b) * c")]
    [InlineData("a - (b - c)")]
    [InlineData("a - b - c")]
    [InlineData("-(a + b)")]
    [InlineData("-(-a)")]
    [InlineData("a * -1")]
    [InlineData("c ? a : b")]
    [InlineData("(c ? a : b) + 1")]
    [InlineData("c ? a : d ? b : e")]
    [InlineData("(c ? a : b) ? 1 : 0")]
    [InlineData("minimum(damage, hp[$each]) * 2 - `seat-1`[hp]")]
    [InlineData("boardShift($board:mask, board, north) & ~boardImage(m, board, rot180)")]
    [InlineData("clamp(v, 0, 10) >> setBitCount(m) == 3 ? 0.5 : 1.25")]
    [InlineData("bitInsert(v, f, 8, 4) | parallelBitExtract(v, 0xFF)")]
    [InlineData("$table:moves:power[$local:move] * $table:armor[$each]")]
    [InlineData("buffs[minion[$each]] + $table:t[minion[owner]]")]
    [Theory]
    public void PrintingIsTheInverseOfParsingWithOnlyTheParenthesesPrecedenceNeeds(string text) {
        var parsed = Parse(text: text);
        var printed = ExpressionSpelling.Print(instructions: parsed);

        Assert.Equal(
            text.Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "255",
                oldValue: "0xFF"
            ),
            printed
        );
        Assert.Equal(
            parsed,
            Parse(text: printed)
        );
    }
    [Fact]
    public void TernaryIsSelectAndAssociatesRight() {
        Assert.Equal(
            [S("c"), S("a"), S("b"), Instruction.Of(operation: ExpressionOp.Select)],
            Parse(text: "c ? a : b")
        );
        Assert.Equal(
            [S("c"), S("a"), S("d"), S("b"), S("e"), Instruction.Of(operation: ExpressionOp.Select), Instruction.Of(operation: ExpressionOp.Select)],
            Parse(text: "c ? a : d ? b : e")
        );
        Assert.Equal(
            [S("$local:x"), C(value: 1m), C(value: 0m), Instruction.Of(operation: ExpressionOp.Select)],
            Parse(text: "$local:x ? 1 : 0")
        );
    }
    [Fact]
    public void UnaryMinusFoldsIntoALiteralAndNegatesAnythingElse() {
        Assert.Equal(
            [C(value: -1m)],
            Parse(text: "-1")
        );
        Assert.Equal(
            [C(value: -0.25m)],
            Parse(text: "-0.25")
        );
        Assert.Equal(
            [S("a"), Instruction.Of(operation: ExpressionOp.Negate)],
            Parse(text: "-a")
        );
        Assert.Equal(
            [S("a"), Instruction.Of(operation: ExpressionOp.BitNot)],
            Parse(text: "~a")
        );
        Assert.Equal(
            [S("a"), C(value: -1m), Instruction.Of(operation: ExpressionOp.Multiply)],
            Parse(text: "a * -1")
        );
        Assert.Equal(
            [C(value: 65280m)],
            Parse(text: "0xFF00")
        );
    }
}
