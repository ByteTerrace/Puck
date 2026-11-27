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

    [Fact]
    public void ASharedFunctionPromotesItsIntArgumentForAFixedParameter() {
        var expression = new ExpressionProgram([Instruction.Operand("score"), Instruction.Call(0)]) {
            Subprograms = [new Subprogram("wave", 1, [Instruction.Argument(0), Instruction.Of(ExpressionOp.Sine)])],
        };
        var compiled = RuleCompiler.CompileExpression(expression, CellKind.Fixed, "promotion", "write", RulesFixture.Context());
        Assert.Contains(compiled.Single(token => token.Operation == ExpressionOp.Call).Call!.Body, token => token.Operation == ExpressionOp.ToFixed);
    }

    [Fact]
    public void StaticAndBoundPoolFieldsPromoteIntCopiesAndExpressions() {
        var section = RulesFixture.Section() with {
            Records = [new StateRecord(Name: RulesFixture.Name("item"), Fields: [
                new StatePoolField(Name: RulesFixture.Name("value"), Kind: CellKind.Fixed, Default: CellValue.Fixed(0)),
            ])],
            Pools = [new StatePool(Name: RulesFixture.Name("items"), Record: RulesFixture.Name("item"), Capacity: 1, Initial: [new StatePoolSeed(Slot: 0)])],
        };
        var catalog = StateCatalog.Compile(section);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var arena = new StateArena(catalog: catalog, section: section, time: ArenaTime.Origin);
        var rule = RuleCompiler.Compile(new Rule(Name: RulesFixture.Name("copy"), Effects: [
            new ActionEffect.SetState(State: StateChannelRef.OfStaticPoolField("items", 0, "value"), FromState: "score"),
            new ActionEffect.ForEachPool(Pool: "items", Binding: RulesFixture.Name("item"), Effects: [
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField("item", "value"), FromState: "score"),
                new ActionEffect.SetState(State: StateChannelRef.OfBindingField("item", "value"), Expression: ExpressionProgram.Parse("score + 1")),
            ]),
        ]), context);
        var evaluator = new RuleEvaluator(new ArenaEffectHost(arena));
        Assert.True(evaluator.Evaluate(rules: [rule], latch: new RuleLatch(), stepTicks: 1));
        var handle = Assert.Single(arena.SnapshotPool(catalog.Pools[0].Ordinal));
        Assert.True(arena.TryRead(handle: handle, fieldOrdinal: 0, value: out var value));
        Assert.Equal(6L << 16, value.AsFixed);
        Assert.Empty(evaluator.Diagnostics());
    }

    [Theory]
    [InlineData("ratio")]
    [InlineData("ratio + 1")]
    [InlineData("1.5")]
    public void FixedNeedsAnExplicitConversionAtAnIntDestination(string text) {
        var error = Assert.Throws<RuleException>(() => RuleCompiler.CompileExpression(ExpressionProgram.Parse(text), CellKind.Int, "promotion", "write", RulesFixture.Context()));
        Assert.Contains("toInt", error.Message);
    }

    [Fact]
    public void AFractionalLiteralCannotBypassTheKindCheckAsADirectWrite() {
        var error = Assert.Throws<RuleException>(() => RulesFixture.Compile(RulesFixture.Rule(name: "copy", effects: [new ActionEffect.SetState(State: "score", Value: 1.5m)])));
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
        Assert.False(ExpressionArithmetic.TryUnary(ExpressionOp.ToFixed, CellKind.Int, 140737488355328L, out _));
        Assert.False(ExpressionArithmetic.TryUnary(ExpressionOp.ToFixed, CellKind.Int, -140737488355329L, out _));
    }
}
