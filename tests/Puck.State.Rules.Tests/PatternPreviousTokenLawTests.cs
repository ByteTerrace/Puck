using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a pattern word's first token has no token before it, and <c>$previous</c> there
/// reads the cell the keyed row does not hold — a zero an expression may compute with — rather than naming no cell,
/// which would fault every arithmetic the value expression spells around it.</summary>
public sealed class PatternPreviousTokenLawTests {
    [Fact]
    public void TheFirstTokensPreviousReadsTheCellTheRowDoesNotHoldRatherThanNoCellAtAll() {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(
            context: context,
            section: section
        );

        Assert.True(
            condition: RuleCompiler.TryCompilePatternValue(
                context: context,
                pattern: new PatternRow(
                    Name: TransformFixture.Name(value: "run"),
                    Kind: CellKind.Int,
                    Symbols: [new PatternSymbol(
                            Name: TransformFixture.Name(value: "one"),
                            Min: 1L,
                            Max: 1L
                        )],
                    Pattern: new PatternNode.Symbol(Name: "one"),
                    Value: RulesFixture.Program(text: "rank[$previous] + 1")
                ),
                reason: out var reason,
                ruleName: "law",
                tokenDomain: "tokens",
                tokens: out var program
            ),
            userMessage: reason
        );

        Span<long> word = stackalloc long[4];
        var length = PatternOperand.ReadTupleWord(
            expression: program!,
            kind: CellKind.Int,
            reader: host,
            rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "deck"
            ),
            word: word
        );

        Assert.Equal(
            actual: word[..length].ToArray(),
            expected: [1L, 4L, 2L]
        );
    }
}
