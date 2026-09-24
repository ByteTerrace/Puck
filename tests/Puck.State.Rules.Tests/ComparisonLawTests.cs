using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>One comparison semantics: a literal compared against a cell lowers through one conversion table,
/// whichever spelling the author reached for.</summary>
public sealed class ComparisonLawTests {
    private static GateToken CompareState(ExpressionOp comparison, decimal literal, string row = "score") => RulesFixture.Compile(rule: RulesFixture.Rule(
        gate: new ActionPredicate.CompareState(
            Comparison: comparison,
            State: row,
            Value: literal
        ),
        name: "compare"
    )).Gate[0];
    private static GateToken CompareValue(ExpressionOp comparison, decimal literal, string row = "score") => RulesFixture.Compile(rule: RulesFixture.Rule(
        gate: new ActionPredicate.CompareValue(
            Comparison: comparison,
            Kind: CellKind.Int,
            Left: RulesFixture.Program(text: row),
            Right: RulesFixture.Program(text: literal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))
        ),
        name: "compare"
    )).Gate[0];

    [Fact]
    public void TheCounterexampleYieldsOneAnswerUnderBothSpellings() {
        var state = CompareState(
            comparison: ExpressionOp.Less,
            literal: 0.5m
        );
        var value = CompareValue(
            comparison: ExpressionOp.Less,
            literal: 0.5m
        );

        Assert.Equal(
            actual: state.Comparison,
            expected: ExpressionOp.LessOrEqual
        );
        Assert.Equal(
            actual: state.RightSource.RawValue,
            expected: 0L
        );
        Assert.Equal(
            actual: value.Comparison,
            expected: ExpressionOp.LessOrEqual
        );
        Assert.Equal(
            actual: value.Constant(),
            expected: 0L
        );
    }
    [InlineData(ExpressionOp.Greater, 1.5, ExpressionOp.GreaterOrEqual, 2L)]
    [InlineData(ExpressionOp.GreaterOrEqual, 1.5, ExpressionOp.GreaterOrEqual, 2L)]
    [InlineData(ExpressionOp.Less, 1.5, ExpressionOp.LessOrEqual, 1L)]
    [InlineData(ExpressionOp.LessOrEqual, 1.5, ExpressionOp.LessOrEqual, 1L)]
    [InlineData(ExpressionOp.Equal, 1.5, ExpressionOp.Greater, long.MaxValue)]
    [InlineData(ExpressionOp.NotEqual, 1.5, ExpressionOp.GreaterOrEqual, long.MinValue)]
    [Theory]
    public void AFractionalLiteralAgainstAnIntCellLowersToTheExactIntegerComparison(ExpressionOp authored, double literal, ExpressionOp lowered, long raw) {
        var token = CompareState(
            comparison: authored,
            literal: ((decimal)literal)
        );

        Assert.Equal(
            actual: token.Comparison,
            expected: lowered
        );
        Assert.Equal(
            actual: token.RightSource.RawValue,
            expected: raw
        );
    }
    [Fact]
    public void AnIntegralLiteralKeepsItsAuthoredComparison() {
        var token = CompareState(
            comparison: ExpressionOp.Less,
            literal: 2m
        );

        Assert.Equal(
            actual: token.Comparison,
            expected: ExpressionOp.Less
        );
        Assert.Equal(
            actual: token.RightSource.RawValue,
            expected: 2L
        );
    }
    [Fact]
    public void AFixedCellKeepsItsExactFixedPointLiteral() {
        var token = CompareState(
            comparison: ExpressionOp.Less,
            literal: 0.5m,
            row: "ratio"
        );

        Assert.Equal(
            actual: token.Comparison,
            expected: ExpressionOp.Less
        );
        Assert.Equal(
            actual: token.RightSource.RawValue,
            expected: Puck.Maths.FixedQ4816.FromDouble(value: 0.5).Value
        );
    }
    [Fact]
    public void BothSpellingsLowerThroughTheOneConversionTable() {
        var (raw, lowered) = RuleCompiler.LowerConstantComparison(
            comparison: ExpressionOp.Less,
            kind: CellKind.Int,
            literal: 0.5m,
            ruleName: "table"
        );

        Assert.Equal(
            actual: lowered,
            expected: ExpressionOp.LessOrEqual
        );
        Assert.Equal(
            actual: raw,
            expected: 0L
        );
    }
    // A comparison is an ExpressionOp from the comparison subset; a gate built in code around any other operation is
    // refused by name at compile, under both spellings, rather than evaluated as some comparison.
    [Fact]
    public void AGateNamingAnOperationThatIsNotAComparisonIsRefused() {
        var state = Assert.Throws<RuleException>(testCode: static () => CompareState(
            comparison: ExpressionOp.Add,
            literal: 1m
        ));
        var value = Assert.Throws<RuleException>(testCode: static () => CompareValue(
            comparison: ExpressionOp.Add,
            literal: 1m
        ));

        Assert.Equal(
            actual: state.Refusal,
            expected: RuleRefusal.PredicateKindInadmissible
        );
        Assert.Equal(
            actual: value.Refusal,
            expected: RuleRefusal.PredicateKindInadmissible
        );
    }
}

/// <summary>Reads the one side of a compiled comparison the expression spelling leaves.</summary>
internal static class GateTokenReadBack {
    /// <summary>Returns the raw constant the comparison's right-hand program carries.</summary>
    /// <param name="token">The compiled token.</param>
    /// <returns>The constant.</returns>
    public static long Constant(this GateToken token) {
        foreach (var instruction in token.Expression()) {
            if (instruction.Operation == ExpressionOp.Constant) {
                return instruction.Constant;
            }
        }

        return 0L;
    }
    /// <summary>Returns the compiled program a <c>compareValue</c> lowered its right-hand side to.</summary>
    /// <param name="token">The compiled token.</param>
    /// <returns>The program.</returns>
    public static CompiledExpressionToken[] Expression(this GateToken token) {
        Assert.True(condition: token.RightSource.IsExpression);

        return token.RightSource.Expression!;
    }
}
