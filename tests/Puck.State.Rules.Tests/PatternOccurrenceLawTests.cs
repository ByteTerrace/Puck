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

        Assert.True(pattern.TryFindOccurrence(word, 0, out var first, out var firstLength));
        Assert.True(pattern.TryFindOccurrence(word, 1, out var second, out var secondLength));
        Assert.False(pattern.TryFindOccurrence(word, 2, out var absent, out var absentLength));
        Assert.Equal(1, first);
        Assert.Equal(3, firstLength);
        Assert.Equal(4, second);
        Assert.Equal(3, secondLength);
        Assert.Equal(-1, absent);
        Assert.Equal(0, absentLength);
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
                Comparison: ActionStateComparison.GreaterOrEqual,
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

        _ = pattern.TryFindOccurrence(word, 1, out _, out _);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var repeat = 0; (repeat < 256); repeat++) {
            _ = pattern.TryFindOccurrence(word, 1, out _, out _);
        }

        Assert.Equal(0L, (GC.GetAllocatedBytesForCurrentThread() - before));
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

                Assert.Equal((expected >= 0), found);
                Assert.Equal(expected, actual);
                Assert.Equal(((expected >= 0) ? 3 : 0), length);
            }
        }
    }
}
