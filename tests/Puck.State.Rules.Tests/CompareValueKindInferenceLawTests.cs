using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A compareValue that declares no kind infers one the way a local's carrier is inferred: Int unless both
/// sides are constants alone and one holds a fraction, and the other domain when the first reading refuses. A
/// declared kind is never second-guessed.</summary>
public sealed class CompareValueKindInferenceLawTests {
    private static CompiledRule Compile(string left, string right, CellKind? kind = null) => RulesFixture.Compile(rule: RulesFixture.Rule(
        gate: new ActionPredicate.CompareValue(
            Comparison: ExpressionOp.Equal,
            Kind: kind,
            Left: RulesFixture.Program(text: left),
            Right: RulesFixture.Program(text: right)
        ),
        name: "compare"
    ));

    [InlineData("score & 4", "4", CellKind.Int)]
    [InlineData("score + 1", "6", CellKind.Int)]
    [InlineData("ratio * 2", "1.5", CellKind.Fixed)]
    [InlineData("3 / 2", "1.5", CellKind.Fixed)]
    [InlineData("3 / 2", "1", CellKind.Int)]
    [Theory]
    public void AnUndeclaredKindIsInferredFromWhatTheSidesRead(string left, string right, CellKind expected) {
        Assert.Equal(expected: expected, actual: Compile(left: left, right: right).Gate[0].ValueKind);
    }
    [Fact]
    public void AComparisonMixingAnIntAndAFixedReadRefusesUnderEitherReading() {
        Assert.Throws<RuleException>(testCode: () => Compile(left: "score + 1", right: "ratio"));
    }
    [Fact]
    public void ADeclaredKindIsKeptEvenWhereTheOtherWouldCompile() {
        Assert.Equal(expected: CellKind.Fixed, actual: Compile(kind: CellKind.Fixed, left: "3 / 2", right: "1").Gate[0].ValueKind);
        Assert.Throws<RuleException>(testCode: () => Compile(kind: CellKind.Fixed, left: "score + 1", right: "6"));
    }
}
