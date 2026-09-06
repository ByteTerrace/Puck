using Xunit;

namespace Puck.State.Tests;

/// <summary>Compile-time arithmetic preserves raw kinds, eager domain refusals, and authored validation.</summary>
public sealed class ConstantFoldingLawTests {
    private static readonly RuleCompileContext Context = new(null, StateCatalog.Compile(null), null, null, null, 240, RuleVocabulary.Core);
    private static CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) =>
        RuleCompiler.CompileExpression(ValueExpression.Parse(text), kind, "fold-law", "fold-law", Context);

    [Theory]
    [InlineData("replicationMask(8)", 0x0101010101010101L)]
    [InlineData("repeatBits(128, 8)", unchecked((long)0x8080808080808080UL))]
    [InlineData("byteSwap(0x5000)", 22517998136852480L)]
    [InlineData("(1 << 63) >>> 63", 1L)]
    [InlineData("bitInsert(0, 15, 4, 4)", 240L)]
    [InlineData("bitField(240, 4, 4)", 15L)]
    [InlineData("clamp(-5, 0, 10)", 0L)]
    [InlineData("1 ? 3 : 7", 3L)]
    [InlineData("pairX(pair(7, 3))", 7L)]
    public void SuccessfulSubtreesBecomeOneRawConstant(string text, long expected) {
        var token = Assert.Single(Compile(text));
        Assert.Equal(ExpressionOp.Constant, token.Operation);
        Assert.Equal(expected, token.Constant);
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("0 * (1 / 0)")]
    [InlineData("1 ? 7 : 1 / 0")]
    [InlineData("0 ? repeatBits(256, 8) : 3")]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("replicationMask(3)")]
    [InlineData("1 << 64")]
    public void ConstantDomainFaultsStillRefuseAtRuntime(string text) {
        Assert.True(Compile(text).Length > 1);
        Assert.False(ExpressionFunctionLawTests.TryEvalPublic(text));
    }

    [Fact]
    public void FixedResultsKeepRawScalingAndIntegerConditions() {
        Assert.Equal(229376L, Assert.Single(Compile("1.5 * 2.25 + 0.125", CellKind.Fixed)).Constant);
        Assert.Equal(98304L, Assert.Single(Compile("1 < 2 ? 1.5 : 2.5", CellKind.Fixed)).Constant);
    }

    [Fact]
    public void IndependentConstantsFoldWithoutElidingAnApparentlyRedundantLiveRead() {
        var program = Compile("$tick * 0 + repeatBits(1, 8)");
        Assert.Equal(new[] { ExpressionOp.Operand, ExpressionOp.Constant, ExpressionOp.Multiply, ExpressionOp.Constant, ExpressionOp.Add },
            program.Select(token => token.Operation));
        Assert.IsType<TickOperand>(program[0].Operand);
        Assert.Equal(0x0101010101010101L, program[3].Constant);
    }

    [Fact]
    public void FoldingCannotHideInvalidNamesKindsOrOversizedAuthoredPrograms() {
        Assert.Throws<RuleException>(() => Compile("1 ? 7 : missing"));
        Assert.Throws<RuleException>(() => Compile("replicationMask(8)", CellKind.Fixed));
        var tokens = new List<ValueToken> { new ValueToken.Constant(0) };
        for (var i = 0; i < RuleCapacity.MaxExpressionTokens; i++) { tokens.Add(new ValueToken.BitNot()); }
        Assert.Throws<RuleException>(() => RuleCompiler.CompileExpression(new ValueExpression(tokens), CellKind.Int, "fold-law", "fold-law", Context));
    }
}
