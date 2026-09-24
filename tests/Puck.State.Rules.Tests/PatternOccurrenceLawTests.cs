using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Rules.Tests;

public sealed class PatternOccurrenceLawTests {
    private static CompiledPattern Pattern() {
        var row = new PatternRow(
            Name: CellName.Parse(candidate: "rule"),
            Kind: CellKind.Int,
            Symbols: [
                new PatternSymbol(CellName.Parse(candidate: "noun"), 1m, 1m),
                new PatternSymbol(CellName.Parse(candidate: "is"), 2m, 2m),
                new PatternSymbol(CellName.Parse(candidate: "property"), 3m, 3m),
            ],
            Pattern: new PatternNode.Sequence(Items: [
                new PatternNode.Symbol(Name: "noun"),
                new PatternNode.Symbol(Name: "is"),
                new PatternNode.Symbol(Name: "property"),
            ])
        );

        Assert.True(
            condition: CompiledPattern.TryCompile(
                compiled: out var compiled,
                reason: out var reason,
                row: row
            ),
            userMessage: reason
        );
        return compiled!;
    }

    [Fact]
    public void OccurrencesRetainTheirStartAndLengthInWordOrder() {
        var pattern = Pattern();
        long[] word = [9, 1, 2, 3, 1, 2, 3, 9];

        Assert.True(condition: pattern.TryFindOccurrence(length: out var firstLength, occurrence: 0, start: out var first, values: word));
        Assert.True(condition: pattern.TryFindOccurrence(length: out var secondLength, occurrence: 1, start: out var second, values: word));
        Assert.False(condition: pattern.TryFindOccurrence(length: out var absentLength, occurrence: 2, start: out var absent, values: word));
        Assert.Equal(actual: first, expected: 1);
        Assert.Equal(actual: firstLength, expected: 3);
        Assert.Equal(actual: second, expected: 4);
        Assert.Equal(actual: secondLength, expected: 3);
        Assert.Equal(actual: absent, expected: -1);
        Assert.Equal(actual: absentLength, expected: 0);
    }
    [InlineData("$match:run:board:E:at:1", MatchFacet.At, 1)]
    [InlineData("$match:run:board:E:length:2", MatchFacet.Length, 2)]
    [InlineData("$match:run:deck:at:3", MatchFacet.At, 3)]
    [InlineData("$match:run:deck:length:4", MatchFacet.Length, 4)]
    [Theory]
    public void OccurrenceFacetSpellingCompiles(string spelling, MatchFacet facet, int occurrence) {
        var context = RulesFixture.Context();
        var rule = RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ExpressionOp.GreaterOrEqual,
                Key: StateChannelRef.OfNullable(spelling: (spelling.Contains(comparisonType: StringComparison.Ordinal, value: ":board:") ? "0" : null)),
                State: spelling,
                Value: 0m
            ),
            name: "occurrence"
        );
        var compiled = RuleCompiler.Compile(context: context, rule: rule);
        var operand = Assert.IsType<PatternOperand>(@object: compiled.Gate[0].LeftSource.Operand);

        Assert.Equal(facet, operand.MatchFacet);
        Assert.Equal(occurrence, operand.Occurrence);
    }
    [InlineData("match(run, board, E, at, 1)[0]")]
    [InlineData("match(run, deck, length, 2)")]
    [Theory]
    public void AuthoredCallsPrintBackWithTheirOccurrence(string source) {
        Assert.True(condition: ExpressionSpelling.TryParse(error: out var error, program: out var program, text: source), userMessage: error);
        Assert.True(condition: ExpressionSpelling.TryPrintSource(program: program!, text: out var printed));
        Assert.Equal(actual: printed, expected: source);
    }
    [Fact]
    public void OccurrenceQueriesAllocateNothingAfterWarmUp() {
        var pattern = Pattern();
        long[] word = [9, 1, 2, 3, 1, 2, 3, 9];

        _ = pattern.TryFindOccurrence(length: out _, occurrence: 1, start: out _, values: word);
        Assert.Equal(0L, AllocationWindow.Least(window: () => {
            for (var repeat = 0; (repeat < 256); repeat++) {
                _ = pattern.TryFindOccurrence(length: out _, occurrence: 1, start: out _, values: word);
            }
        }));
    }
    [Fact]
    public void NounIsPropertyOccurrencesAgreeWithBruteForceAcrossBoardAxes() {
        var pattern = Pattern();
        long[,] board = {
            { 1, 2, 3, 9, 1 },
            { 9, 9, 1, 9, 2 },
            { 1, 9, 2, 9, 3 },
            { 2, 9, 3, 9, 9 },
            { 3, 9, 9, 9, 9 },
        };

        Span<long> word = stackalloc long[5];

        for (var axis = 0; (axis < 2); axis++) {
            for (var line = 0; (line < board.GetLength(dimension: axis)); line++) {
                for (var cell = 0; (cell < word.Length); cell++) {
                    word[cell] = ((axis == 0) ? board[line, cell] : board[cell, line]);
                }

                var expected = -1;

                for (var start = 0; (start <= (word.Length - 3)); start++) {
                    if ((word[start] == 1) && (word[(start + 1)] == 2) && (word[(start + 2)] == 3)) {
                        expected = start;
                        break;
                    }
                }

                var found = pattern.TryFindOccurrence(word, occurrence: 0, out var actual, out var length);

                Assert.Equal(actual: found, expected: (expected >= 0));
                Assert.Equal(actual: actual, expected: expected);
                Assert.Equal(actual: length, expected: ((expected >= 0) ? 3 : 0));
            }
        }
    }
}
