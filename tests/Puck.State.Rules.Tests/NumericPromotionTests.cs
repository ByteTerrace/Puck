using Xunit;

namespace Puck.State.Rules.Tests;

public sealed class NumericPromotionTests {
    [Theory]
    [InlineData("3", 196608L)]
    [InlineData("1 / 2", 32768L)]
    [InlineData("3 + 0.5", 229376L)]
    [InlineData("1 ? 3 : 0.5", 196608L)]
    [InlineData("squareRoot(4)", 131072L)]
    [InlineData("toFixed(1.5)", 98304L)]
    [InlineData("toInt(-1.5)", -65536L)]
    public void AnIntExpressionWidensExactlyAtAFixedDestination(string text, long expected) {
        var compiled = RuleCompiler.CompileExpression(ExpressionProgram.Parse(text), CellKind.Fixed, "promotion", "write", RulesFixture.Context());
        Assert.True(RuleExpressions.EvaluateDetached(null, compiled, CellKind.Fixed).TryFinite(out var raw));
        Assert.Equal(expected, raw);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("score + ratio")]
    [InlineData("minimum(score, ratio)")]
    [InlineData("score > 0 ? score : ratio")]
    [InlineData("squareRoot(score)")]
    public void LiveIntReadsPromoteInsideFixedExpressions(string text) {
        var compiled = RuleCompiler.CompileExpression(ExpressionProgram.Parse(text), CellKind.Fixed, "promotion", "write", RulesFixture.Context());
        Assert.Contains(compiled, token => token.Operation == ExpressionOp.ToFixed);
    }

    [Fact]
    public void ADirectIntCopyUsesTheSameConversionAsAnExpression() {
        var compiled = RulesFixture.Compile(RulesFixture.Rule(name: "copy", effects: [new ActionEffect.SetState(State: "ratio", FromState: "score")]));
        var write = Assert.IsType<WriteEffect>(compiled.Effects[0]);
        Assert.Contains(write.Source.Expression!, token => token.Operation == ExpressionOp.ToFixed);
    }

    [Theory]
    [InlineData("ratio")]
    [InlineData("ratio + 1")]
    [InlineData("1.5")]
    public void FixedNeedsAnExplicitConversionAtAnIntDestination(string text) {
        var error = Assert.Throws<RuleException>(() => RuleCompiler.CompileExpression(ExpressionProgram.Parse(text), CellKind.Int, "promotion", "write", RulesFixture.Context()));
        Assert.Contains("toInt", error.Message);
    }

    [Theory]
    [InlineData("140737488355327", 9223372036854710272L)]
    [InlineData("-140737488355328", long.MinValue)]
    public void PromotionReachesBothQ4816IntegerLimitsExactly(string text, long expected) {
        var compiled = RuleCompiler.CompileExpression(ExpressionProgram.Parse(text), CellKind.Fixed, "promotion", "write", RulesFixture.Context());
        Assert.True(RuleExpressions.EvaluateDetached(null, compiled, CellKind.Fixed).TryFinite(out var raw));
        Assert.Equal(expected, raw);
    }

    [Fact]
    public void PromotionOutsideQ4816FaultsInsteadOfWrapping() {
        var compiled = RuleCompiler.CompileExpression(ExpressionProgram.Parse("140737488355328"), CellKind.Fixed, "promotion", "write", RulesFixture.Context());
        Assert.Equal(FaultCase.Faulted, RuleExpressions.EvaluateDetached(null, compiled, CellKind.Fixed).Case);
    }
}
