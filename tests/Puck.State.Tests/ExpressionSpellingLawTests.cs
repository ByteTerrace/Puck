using Xunit;

namespace Puck.State.Tests;

/// <summary>The infix spelling is syntax over the postfix tokens: C precedence parses to the tokens an author would
/// write by hand, and every token kind prints to a spelling that parses back to itself with only the parentheses
/// precedence needs. The world document's converter and schema facts live in <c>tests/Puck.World.Schema.Tests</c>.</summary>
public sealed class ExpressionSpellingLawTests {
    private static ValueToken S(string name, string? key = null) => new ValueToken.State(name, key);
    private static ValueToken C(decimal value) => new ValueToken.Constant(value);

    private static IReadOnlyList<ValueToken> Parse(string text) {
        Assert.True(ExpressionSpelling.TryParse(text, out var tokens, out var error), error);
        return tokens;
    }

    [Theory]
    [InlineData("a + b * c", "a", "b", "c", "multiply", "add")]
    [InlineData("(a + b) * c", "a", "b", "add", "c", "multiply")]
    [InlineData("a - b - c", "a", "b", "subtract", "c", "subtract")]
    [InlineData("a << 2 & b", "a", "2", "shiftLeft", "b", "bitAnd")]
    [InlineData("a == b | c", "a", "b", "equal", "c", "bitOr")]
    [InlineData("a >>> 1 ^ b", "a", "1", "shiftRightLogical", "b", "bitXor")]
    [InlineData("a % 3 >= b", "a", "3", "modulo", "b", "greaterOrEqual")]
    public void PrecedenceFollowsC(string text, params string[] expected) {
        Assert.Equal(expected, Parse(text).Select(Spell));
    }

    [Fact]
    public void UnaryMinusFoldsIntoALiteralAndNegatesAnythingElse() {
        Assert.Equal([C(-1m)], Parse("-1"));
        Assert.Equal([C(-0.25m)], Parse("-0.25"));
        Assert.Equal([S("a"), new ValueToken.Negate()], Parse("-a"));
        Assert.Equal([S("a"), new ValueToken.BitNot()], Parse("~a"));
        Assert.Equal([S("a"), C(-1m), new ValueToken.Multiply()], Parse("a * -1"));
        Assert.Equal([C(65280m)], Parse("0xFF00"));
    }

    [Fact]
    public void TernaryIsSelectAndAssociatesRight() {
        Assert.Equal([S("c"), S("a"), S("b"), new ValueToken.Select()], Parse("c ? a : b"));
        Assert.Equal(
            [S("c"), S("a"), S("d"), S("b"), S("e"), new ValueToken.Select(), new ValueToken.Select()],
            Parse("c ? a : d ? b : e")
        );
        Assert.Equal([S("$bind:x"), C(1m), C(0m), new ValueToken.Select()], Parse("$bind:x ? 1 : 0"));
    }

    [Fact]
    public void NamesKeysCallsAndBoardOpsSpellTheirTokens() {
        Assert.Equal([S("hp", "$each")], Parse("hp[$each]"));
        Assert.Equal([S("seat-1", "0")], Parse("`seat-1`[0]"));
        Assert.Equal([S("$table:armor:$each"), C(2m), new ValueToken.Multiply()], Parse("$table:armor:$each * 2"));
        Assert.Equal([S("$table:armor:$each")], Parse("$table:armor[$each]"));
        Assert.Equal([S("$table:moves:power:$bind:move")], Parse("$table:moves:power[$bind:move]"));
        Assert.Equal([S("$table:moves:power:$cell:turn:move")], Parse("$table:moves:power[$cell:turn:move]"));
        Assert.Equal([S("$table:armor:7")], Parse("$table:armor[7]"));
        Assert.Equal([S("buffs", "$cell:minion:$each")], Parse("buffs[minion[$each]]"));
        Assert.Equal([S("buffs", "$cell:minion:$cell:squad:$each")], Parse("buffs[minion[squad[$each]]]"));
        Assert.Equal([S("$table:t:$cell:minion:$each")], Parse("$table:t[minion[$each]]"));
        Assert.Equal("buffs[minion[$each]]", ExpressionSpelling.Print([S("buffs", "$cell:minion:$each")]));
        Assert.Equal("$table:moves:power[$bind:move] + $table:armor[7]", ExpressionSpelling.Print([S("$table:moves:power:$bind:move"), S("$table:armor:7"), new ValueToken.Add()]));
        Assert.Equal([S("damage"), S("hp"), new ValueToken.Min()], Parse("min(damage, hp)"));
        Assert.Equal([S("v"), C(0m), C(10m), new ValueToken.Clamp()], Parse("clamp(v, 0, 10)"));
        Assert.Equal([S("v"), C(8m), C(4m), new ValueToken.BitField()], Parse("bitField(v, 8, 4)"));
        Assert.Equal([S("m"), new ValueToken.BoardShift("board", "north")], Parse("boardShift(m, board, north)"));
        Assert.Equal([S("m"), new ValueToken.BoardImage("board", "rot180")], Parse("boardImage(m, board, rot180)"));
        Assert.Equal([S("m"), new ValueToken.PopCount()], Parse("popCount(m)"));
        Assert.Equal([S("c"), S("a"), S("b"), new ValueToken.Select()], Parse("select(c, a, b)"));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("a +", "reached the end")]
    [InlineData("foo(1)", "not a function")]
    [InlineData("min(1)", "expected ','")]
    [InlineData("min(1, 2, 3)", "argument")]
    [InlineData("1 2", "unexpected '2'")]
    [InlineData("`open", "not closed")]
    [InlineData("a ? b", "expected ':'")]
    [InlineData("a # b", "unexpected character '#'")]
    [InlineData("boardShift(m, board)", "expected ','")]
    public void AMalformedSpellingIsRefusedByName(string text, string expected) {
        Assert.False(ExpressionSpelling.TryParse(text, out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
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
    [InlineData("min(damage, hp[$each]) * 2 - `seat-1`[hp]")]
    [InlineData("boardShift($board:mask, board, north) & ~boardImage(m, board, rot180)")]
    [InlineData("clamp(v, 0, 10) >> popCount(m) == 3 ? 0.5 : 1.25")]
    [InlineData("bitInsert(v, f, 8, 4) | parallelBitExtract(v, 0xFF)")]
    [InlineData("$table:moves:power[$bind:move] * $table:armor[$each]")]
    [InlineData("buffs[minion[$each]] + $table:t[minion[owner]]")]
    public void PrintingIsTheInverseOfParsingWithOnlyTheParenthesesPrecedenceNeeds(string text) {
        var tokens = Parse(text);
        var printed = ExpressionSpelling.Print(tokens);
        Assert.Equal(text.Replace("0xFF", "255", StringComparison.Ordinal), printed);
        Assert.Equal(tokens, Parse(printed));
    }

    [Fact]
    public void AMalformedPostfixListDoesNotPrint() {
        Assert.False(ExpressionSpelling.TryPrint([new ValueToken.Add()], out _));
        Assert.False(ExpressionSpelling.TryPrint([C(1m), C(2m)], out _));
    }

    private static string Spell(ValueToken token) => token switch {
        ValueToken.State state => state.Name,
        ValueToken.Constant constant => constant.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => char.ToLowerInvariant(token.GetType().Name[0]) + token.GetType().Name[1..],
    };
}
