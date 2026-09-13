using Xunit;

namespace Puck.State.Tests;

/// <summary>The infix spelling is syntax over the postfix tokens: C precedence parses to the tokens an author would
/// write by hand, and every token kind prints to a spelling that parses back to itself with only the parentheses
/// precedence needs. The world document's converter and schema facts live in <c>tests/Puck.World.Schema.Tests</c>.</summary>
public sealed class ExpressionSpellingLawTests {
    private static ValueToken C(decimal value) => new ValueToken.Constant(Value: value);
    private static IReadOnlyList<ValueToken> Parse(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                tokens: out var tokens
            ),
            userMessage: error
        );
        return tokens;
    }
    private static ValueToken S(string name, string? key = null) => new ValueToken.State(
        Key: key,
        Name: name
    );
    private static string Spell(ValueToken token) => token switch {
        ValueToken.State state => state.Name,
        ValueToken.Constant constant => constant.Value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        _ => (char.ToLowerInvariant(c: token.GetType().Name[0]) + token.GetType().Name[1..]),
    };

    [Fact]
    public void AMalformedPostfixListDoesNotPrint() {
        Assert.False(condition: ExpressionSpelling.TryPrint(
            [new ValueToken.Add()],
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
            tokens: out _
        ));
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: expected
        );
    }
    [Fact]
    public void EveryContextFreeTokenHasOneRoundTrippingSpellingAndCompiles() {
        var context = new RuleCompileContext(
            null,
            StateCatalog.Compile(section: null),
            null,
            null,
            null,
            240,
            RuleVocabulary.Core
        );
        var types = typeof(ValueToken).GetNestedTypes().Where(predicate: type => (type.IsSubclassOf(c: typeof(ValueToken)) && (type.GetConstructor(types: Type.EmptyTypes) is not null))).ToArray();
        // The other opcodes carry literal, operand, or topology payloads.
        Assert.Equal(
            (Enum.GetValues<ExpressionOp>().Length - 5),
            types.Length
        );
        foreach (var type in types) {
            var token = ((ValueToken)Activator.CreateInstance(type: type)!);
            IReadOnlyList<ValueToken>? expression = null;

            for (var arity = 1; (arity <= 4); arity++) {
                ValueToken[] candidate = [.. Enumerable.Repeat(
                        S("v"),
                        arity
                    ), token];

                if (!ExpressionSpelling.TryPrint(
                    text: out var text,
                    tokens: candidate
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
                    _ = RuleCompiler.CompileExpression(
                        new ValueExpression(Tokens: [.. expression.Select(selector: item => ((item is ValueToken.State)
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
                userMessage: $"No numeric kind admits {type.Name}"
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
            [S("$table:armor:$each"), C(value: 2m), new ValueToken.Multiply()],
            Parse(text: "$table:armor:$each * 2")
        );
        Assert.Equal(
            [S("$board:mask:board:-6:-6"), C(value: 1m), new ValueToken.Subtract()],
            Parse(text: "$board:mask:board:-6:-6 - 1")
        );
        Assert.Equal(
            "$board:mask:board:-6:-6 - 1",
            ExpressionSpelling.Print(tokens: [S("$board:mask:board:-6:-6"), C(value: 1m), new ValueToken.Subtract()])
        );
        Assert.Equal(
            [S("$table:armor:$each")],
            Parse(text: "$table:armor[$each]")
        );
        Assert.Equal(
            [S("$table:moves:power:$bind:move")],
            Parse(text: "$table:moves:power[$bind:move]")
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
            ExpressionSpelling.Print(tokens: [S(
                    key: "$cell:minion:$each",
                    name: "buffs"
                )])
        );
        Assert.Equal(
            "$table:moves:power[$bind:move] + $table:armor[7]",
            ExpressionSpelling.Print(tokens: [S("$table:moves:power:$bind:move"), S("$table:armor:7"), new ValueToken.Add()])
        );
        Assert.Equal(
            [S("damage"), S("hp"), new ValueToken.Min()],
            Parse(text: "minimum(damage, hp)")
        );
        Assert.Equal(
            [S("v"), C(value: 0m), C(value: 10m), new ValueToken.Clamp()],
            Parse(text: "clamp(v, 0, 10)")
        );
        Assert.Equal(
            [S("v"), C(value: 8m), C(value: 4m), new ValueToken.BitField()],
            Parse(text: "bitField(v, 8, 4)")
        );
        Assert.Equal(
            [S("m"), new ValueToken.BoardShift(
                    Direction: "north",
                    Topology: "board"
                )],
            Parse(text: "boardShift(m, board, north)")
        );
        Assert.Equal(
            [S("m"), new ValueToken.BoardFill(
                    Direction: "north",
                    Topology: "board"
                )],
            Parse(text: "boardFill(m, board, north)")
        );
        Assert.Equal(
            [S("m"), new ValueToken.BoardImage(
                    Element: "rot180",
                    Topology: "board"
                )],
            Parse(text: "boardImage(m, board, rot180)")
        );
        Assert.Equal(
            [S("m"), new ValueToken.PopCount()],
            Parse(text: "setBitCount(m)")
        );
        Assert.Equal(
            [S("c"), S("a"), S("b"), new ValueToken.Select()],
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
    [InlineData("$table:moves:power[$bind:move] * $table:armor[$each]")]
    [InlineData("buffs[minion[$each]] + $table:t[minion[owner]]")]
    [Theory]
    public void PrintingIsTheInverseOfParsingWithOnlyTheParenthesesPrecedenceNeeds(string text) {
        var tokens = Parse(text: text);
        var printed = ExpressionSpelling.Print(tokens: tokens);

        Assert.Equal(
            text.Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "255",
                oldValue: "0xFF"
            ),
            printed
        );
        Assert.Equal(
            tokens,
            Parse(text: printed)
        );
    }
    [Fact]
    public void TernaryIsSelectAndAssociatesRight() {
        Assert.Equal(
            [S("c"), S("a"), S("b"), new ValueToken.Select()],
            Parse(text: "c ? a : b")
        );
        Assert.Equal(
            [S("c"), S("a"), S("d"), S("b"), S("e"), new ValueToken.Select(), new ValueToken.Select()],
            Parse(text: "c ? a : d ? b : e")
        );
        Assert.Equal(
            [S("$bind:x"), C(value: 1m), C(value: 0m), new ValueToken.Select()],
            Parse(text: "$bind:x ? 1 : 0")
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
            [S("a"), new ValueToken.Negate()],
            Parse(text: "-a")
        );
        Assert.Equal(
            [S("a"), new ValueToken.BitNot()],
            Parse(text: "~a")
        );
        Assert.Equal(
            [S("a"), C(value: -1m), new ValueToken.Multiply()],
            Parse(text: "a * -1")
        );
        Assert.Equal(
            [C(value: 65280m)],
            Parse(text: "0xFF00")
        );
    }
}
